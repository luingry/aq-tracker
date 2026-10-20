using System.Diagnostics;
using System.IO;
using AqTracker.Core;

namespace AqTracker;

/// <summary>Commands the SwissTechKnife module page sends to a running tracker.</summary>
public enum HostCommand { Show, Settings, Refresh }

/// <summary>
/// Options for running under the SwissTechKnife host (<c>--hosted</c>). The host's AQ Tracker module
/// launches this process and owns tray, updates and auto-start, so hosted mode turns those off,
/// keeps its data in the module's data folder and exits with its parent. Tracking itself is unchanged.
/// </summary>
public sealed class HostedMode : IDisposable
{
    public const string HostedArg = "--hosted";
    public const string ParentArg = "--parent";
    public const string DataArg = "--data";
    public const string ShowArg = "--show";
    public const string OpenSettingsArg = "--open-settings";

    /// <summary>Exit code when another tracker (standalone) already owns the single-instance mutex.</summary>
    public const int AlreadyRunningExitCode = 3;

    // Named objects hang off the per-user single-instance mutex name; the module derives the same names.
    public static string ExitEventName(string mutexName) => mutexName + ".Hosted.Exit";
    public static string CommandEventName(string mutexName, HostCommand command) => mutexName + "." + command;

    /// <summary>Files carried over from the standalone data folder on the first hosted run.</summary>
    private static readonly string[] StandaloneDataFiles =
    [
        "settings.json", "settings.json.bak", "claude-tokens.dat", "claude-quota.json",
        "quota-history.json", "quota-history.initial.json",
    ];

    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitRegistration;
    private Process? _parent;
    private int _exitRaised;

    private HostedMode(int parentId, string dataDirectory, bool show, bool openSettings)
    {
        ParentId = parentId;
        DataDirectory = dataDirectory;
        ShowOnStart = show || openSettings;
        OpenSettingsOnStart = openSettings;
    }

    public int ParentId { get; }
    public string DataDirectory { get; }
    public bool ShowOnStart { get; }
    public bool OpenSettingsOnStart { get; }

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Null unless the command line carries <c>--hosted</c> with a rooted <c>--data</c> folder.</summary>
    public static HostedMode? TryParse(IReadOnlyList<string> args)
    {
        var hosted = false;
        var show = false;
        var openSettings = false;
        var parent = 0;
        string? data = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, HostedArg, StringComparison.OrdinalIgnoreCase)) hosted = true;
            else if (string.Equals(arg, ShowArg, StringComparison.OrdinalIgnoreCase)) show = true;
            else if (string.Equals(arg, OpenSettingsArg, StringComparison.OrdinalIgnoreCase)) openSettings = true;
            else if (string.Equals(arg, ParentArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) int.TryParse(args[++i], out parent);
            else if (string.Equals(arg, DataArg, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) data = args[++i];
        }
        if (!hosted || data is null || string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data)) return null;
        return new HostedMode(parent, data, show, openSettings);
    }

    /// <summary>
    /// First hosted run (no settings.json in the module folder yet): copies the standalone data so the user
    /// keeps preferences, the Claude sign-in (DPAPI current-user, so a plain copy stays readable) and history.
    /// The standalone folder is never modified.
    /// </summary>
    public void MigrateStandaloneData() =>
        MigrateFrom(Path.Combine(UserFolders.ApplicationData, LegacyDataMigration.FolderName), DataDirectory);

    /// <summary>Copies the known data files from <paramref name="standaloneDirectory"/> unless <paramref name="dataDirectory"/> is in use.</summary>
    public static bool MigrateFrom(string standaloneDirectory, string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (File.Exists(Path.Combine(dataDirectory, "settings.json")) || !Directory.Exists(standaloneDirectory)) return false;
            var copied = 0;
            foreach (var name in StandaloneDataFiles)
            {
                var source = Path.Combine(standaloneDirectory, name);
                var destination = Path.Combine(dataDirectory, name);
                if (!File.Exists(source) || File.Exists(destination)) continue;
                File.Copy(source, destination);
                copied++;
            }
            SanitizedLogger.Write("Hosted data migrated from standalone folder; files=" + copied);
            return copied > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Defaults are used instead; the standalone files stay untouched.
            SanitizedLogger.Write("Hosted data migration failed: " + error.GetType().Name);
            return false;
        }
    }

    /// <summary>Calls <paramref name="exit"/> once (from any thread) when the host exits or the module asks this process to exit.</summary>
    public void WatchForExit(string mutexName, Action exit)
    {
        void RaiseOnce() { if (Interlocked.Exchange(ref _exitRaised, 1) == 0) exit(); }
        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ExitEventName(mutexName));
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => RaiseOnce(), null, Timeout.Infinite, executeOnlyOnce: true);

        if (ParentId <= 0) return;
        try
        {
            _parent = Process.GetProcessById(ParentId);
            _parent.EnableRaisingEvents = true;
            _parent.Exited += (_, _) => RaiseOnce();
            if (_parent.HasExited) RaiseOnce();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The parent is already gone.
            RaiseOnce();
        }
    }

    public void Dispose()
    {
        _exitRegistration?.Unregister(null);
        _exitEvent?.Dispose();
        _parent?.Dispose();
    }
}

/// <summary>
/// Listens for <see cref="HostCommand"/> events (auto-reset, per user and session). The module page signals
/// them to show the widget, open its settings or refresh, which replaces the tray menu while hosted. Nothing
/// happens unless someone signals, so the standalone app listens too (the module page then still works while a
/// standalone tracker owns the quota polling).
/// </summary>
public sealed class HostCommandListener : IDisposable
{
    private readonly List<(EventWaitHandle Handle, RegisteredWaitHandle Registration)> _waits = [];

    public HostCommandListener(string mutexName, Action<HostCommand> received)
    {
        foreach (HostCommand command in Enum.GetValues(typeof(HostCommand)))
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, HostedMode.CommandEventName(mutexName, command));
            var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => received(command), null, Timeout.Infinite, executeOnlyOnce: false);
            _waits.Add((handle, registration));
        }
    }

    public void Dispose()
    {
        foreach (var (handle, registration) in _waits)
        {
            registration.Unregister(null);
            handle.Dispose();
        }
        _waits.Clear();
    }
}
