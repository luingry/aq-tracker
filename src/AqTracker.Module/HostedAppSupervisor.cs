using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SwissTechKnife.Sdk;

namespace AqTracker.Module;

public enum HostedAppState
{
    Stopped,
    Starting,
    Running,
    /// <summary>A standalone AQ Tracker owns the single-instance mutex; the module waits for it to exit.</summary>
    RunningOutside,
    /// <summary>The app exited repeatedly; restarts stopped until the user shows it again.</summary>
    Failed,
}

/// <summary>Tray-menu equivalents sent to the running tracker. Names must match AqTracker.HostCommand.</summary>
public enum TrackerCommand { Show, Settings, Refresh }

/// <summary>
/// Runs the AqTracker widget in its own process (<c>--hosted</c>), so its WPF window, SQLite, System.Text.Json 8
/// and Codex app-server child never share the host's UI thread, assemblies or crashes. Restarts it after a crash
/// (bounded) and stops it with the module. Thread-safe; <see cref="StateChanged"/> is raised on arbitrary threads.
/// </summary>
internal sealed class HostedAppSupervisor : IDisposable
{
    // Must match AqTracker.App / HostedMode: exit code and the names derived from the per-user mutex.
    private const int AlreadyRunningExitCode = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(1);
    private const int MaxCrashesPerWindow = 3;
    private static readonly TimeSpan OutsidePollInterval = TimeSpan.FromSeconds(5);
    private const int ExitTimeoutMs = 4000;

    private readonly object _gate = new();
    private readonly string _exePath;
    private readonly string _dataDirectory;
    private readonly IModuleLog _log;
    private readonly string _mutexName;
    private readonly Queue<DateTime> _crashes = new();
    private Process? _process;
    private Timer? _retry;
    private bool _stopping;
    private bool _disposed;
    private HostedAppState _state = HostedAppState.Stopped;

    public HostedAppSupervisor(string exePath, string dataDirectory, IModuleLog log)
    {
        _exePath = exePath;
        _dataDirectory = dataDirectory;
        _log = log;
        using var identity = WindowsIdentity.GetCurrent();
        _mutexName = @"Local\AqTracker.User." + identity.User!.Value;
    }

    public event Action<HostedAppState>? StateChanged;

    public HostedAppState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Launches the app hidden on a pool thread; returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopping = false;
        }
        ThreadPool.QueueUserWorkItem(_ => Launch(TrackerCommand.Refresh, show: false));
    }

    /// <summary>
    /// Sends <paramref name="command"/> to the running tracker (hosted or standalone); starts the hosted app first
    /// when nothing is running, showing the widget (and its settings) unless the command is a refresh.
    /// </summary>
    public void Send(TrackerCommand command)
    {
        HostedAppState state;
        lock (_gate)
        {
            if (_disposed || _stopping) return;
            state = _state;
        }

        if (state is HostedAppState.Running or HostedAppState.RunningOutside or HostedAppState.Starting)
        {
            // The host is in the foreground (the user clicked); let the tracker take focus.
            AllowSetForegroundWindow(AsfwAny);
            if (SignalNamedEvent(_mutexName + "." + command))
                return;
            if (state is HostedAppState.Starting or HostedAppState.Running && command == TrackerCommand.Refresh)
                return; // Still starting: it refreshes on its own.
        }

        lock (_gate) _crashes.Clear();
        ThreadPool.QueueUserWorkItem(_ => Launch(command, show: command != TrackerCommand.Refresh));
    }

    /// <summary>Asks the app to save and exit; kills it after a short timeout. Blocks at most ~5 s.</summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            _stopping = true;
            _retry?.Dispose();
            _retry = null;
            process = _process;
        }

        if (process is not null)
        {
            try
            {
                using var exit = new EventWaitHandle(false, EventResetMode.ManualReset, _mutexName + ".Hosted.Exit");
                exit.Set();
                if (!process.WaitForExit(ExitTimeoutMs))
                {
                    _log.Warn("AQ Tracker did not exit in time; terminating it.");
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }

        SetState(HostedAppState.Stopped);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate) _disposed = true;
    }

    private void Launch(TrackerCommand command, bool show)
    {
        lock (_gate)
        {
            if (_disposed || _stopping || _process is not null) return;
            _retry?.Dispose();
            _retry = null;
        }

        // No hosted process is ours, so a held mutex means a standalone tracker: never poll Claude/Codex twice.
        if (IsStandaloneRunning())
        {
            if (show)
            {
                AllowSetForegroundWindow(AsfwAny);
                SignalNamedEvent(_mutexName + "." + command);
            }
            EnterRunningOutside();
            return;
        }

        if (!File.Exists(_exePath))
        {
            _log.Error("AQ Tracker executable not found: " + _exePath);
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        try
        {
            using var current = Process.GetCurrentProcess();
            var args = $"--hosted --parent {current.Id} --data \"{_dataDirectory.TrimEnd('\\')}\"";
            if (show)
            {
                args += command == TrackerCommand.Settings ? " --open-settings" : " --show";
                AllowSetForegroundWindow(AsfwAny);
            }
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(_exePath, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += OnExited;
            lock (_gate)
            {
                if (_stopping || _disposed)
                {
                    process.Dispose();
                    SetState(HostedAppState.Stopped);
                    return;
                }
                process.Start();
                _process = process;
            }
            _log.Info($"AQ Tracker started (pid {process.Id}).");
            SetState(HostedAppState.Running);
        }
        catch (Exception error)
        {
            _log.Error("Could not start AQ Tracker.", error);
            SetState(HostedAppState.Failed);
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var process = (Process)sender!;
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        process.Dispose();

        bool stopping;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
                _process = null;
            stopping = _stopping || _disposed;
        }
        if (stopping)
            return;

        if (code == AlreadyRunningExitCode)
        {
            EnterRunningOutside();
            return;
        }

        _log.Warn($"AQ Tracker exited unexpectedly (code {code}).");
        int recent;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _crashes.Enqueue(now);
            while (_crashes.Count > 0 && now - _crashes.Peek() > CrashWindow)
                _crashes.Dequeue();
            recent = _crashes.Count;
        }

        if (recent >= MaxCrashesPerWindow)
        {
            _log.Error("AQ Tracker keeps exiting; automatic restarts stopped.");
            SetState(HostedAppState.Failed);
            return;
        }

        SetState(HostedAppState.Starting);
        ScheduleLaunch(TimeSpan.FromSeconds(recent));
    }

    /// <summary>Polls cheaply (mutex existence only) until the standalone tracker exits, then takes over.</summary>
    private void EnterRunningOutside()
    {
        SetState(HostedAppState.RunningOutside);
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ =>
            {
                if (!IsStandaloneRunning())
                    Launch(TrackerCommand.Refresh, show: false);
            }, null, OutsidePollInterval, OutsidePollInterval);
        }
    }

    private void ScheduleLaunch(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_stopping || _disposed) return;
            _retry?.Dispose();
            _retry = new Timer(_ => Launch(TrackerCommand.Refresh, show: false), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SetState(HostedAppState state)
    {
        lock (_gate)
        {
            if (_state == state) return;
            _state = state;
        }
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception error)
        {
            _log.Error("State handler failed.", error);
        }
    }

    /// <summary>
    /// The tracker's per-user single-instance mutex exists while any tracker runs. Only called while this module
    /// has no hosted process, so it then means a standalone (installed or dev) tracker. Never acquires it.
    /// </summary>
    private bool IsStandaloneRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(_mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Exists, just not openable with these rights.
        }
    }

    private static bool SignalNamedEvent(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
