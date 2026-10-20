namespace AqTracker;
public partial class App : System.Windows.Application
{
    private readonly bool _launchMainWindow;
    public App() : this(true) { }
    // WPF schedules OnStartup even when a UI harness does not call Run().
    public App(bool launchMainWindow) => _launchMainWindow = launchMainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _shutdownEvent;
    private HostedMode? _hosted;
    private HostCommandListener? _commands;

    /// <summary>
    /// Multicore JIT replays the previous run's startup JIT on spare cores, so a sign-in start
    /// competing with every other startup app compiles less on the UI thread. NGen would need
    /// elevation, which this per-user install never asks for. Any failure just means normal JIT.
    /// </summary>
    private static void StartJitProfile(bool hosted)
    {
        try
        {
            var directory = System.IO.Path.Combine(AqTracker.Core.UserFolders.LocalApplicationData, "AqTracker", "jit");
            System.IO.Directory.CreateDirectory(directory);
            System.Runtime.ProfileOptimization.SetProfileRoot(directory);
            System.Runtime.ProfileOptimization.StartProfile(hosted ? "startup.hosted.jitprofile" : "startup.jitprofile");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException) { }
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (!_launchMainWindow) { base.OnStartup(e); return; }
        // Hosted (SwissTechKnife module): the host owns tray, updates and auto-start.
        _hosted = HostedMode.TryParse(e.Args);
        var hosted = _hosted is not null;
        var mutexName = GetSingleInstanceMutexName();
        if (!hosted && e.Args.Contains("--shutdown-existing", StringComparer.OrdinalIgnoreCase))
        {
            SignalExistingInstanceShutdown(mutexName);
            Shutdown();
            return;
        }

        // Same mutex in both modes: standalone and hosted trackers never poll or rotate tokens together.
        _singleInstanceMutex = new Mutex(true, mutexName, out var firstInstance);
        if (!firstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(hosted ? HostedMode.AlreadyRunningExitCode : 0);
            return;
        }
        // Only the surviving instance records: a duplicate that exits at once would overwrite the profile.
        StartJitProfile(hosted);

        if (_hosted is not null)
        {
            // The module's exit event (and the host process exiting) replace the installer's shutdown
            // event: the standalone installer's --shutdown-existing must not stop a hosted tracker.
            _hosted.WatchForExit(mutexName, () => Dispatcher.BeginInvoke(new Action(() => Shutdown())));
        }
        else
        {
            var shutdownEventName = GetShutdownEventName();
            _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shutdownEventName);
            _ = Task.Run(() =>
            {
                _shutdownEvent.WaitOne();
                Dispatcher.Invoke(Shutdown);
            });
        }
        // The module page's show/settings/refresh buttons replace the tray menu while hosted.
        _commands = new HostCommandListener(mutexName, command =>
            Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.HandleHostCommand(command))));

        base.OnStartup(e);
        AqTracker.Core.SanitizedLogger.Write("Application startup: " + typeof(App).Assembly.GetName().Version + "; startup=" + e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase) + "; hosted=" + hosted);
        AqTracker.Core.LegacyDataMigration.MigrateRoamingData();
        _hosted?.MigrateStandaloneData();
        var store = new SettingsStore(_hosted?.SettingsPath);
        if (_hosted is not null) AqTracker.Core.SanitizedLogger.Write("Hosted data folder: " + _hosted.DataDirectory);
        AppSettings settings;
        try { settings = await store.LoadWhenAvailableAsync(); }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            if (_singleInstanceMutex is null) return;
            AqTracker.Core.SanitizedLogger.Write("Startup stopped to preserve settings: " + error.GetType().Name);
            System.Windows.MessageBox.Show("Não foi possível ler as preferências do Agent Quota Tracker. Os dados foram preservados. Tente abrir o aplicativo novamente.",
                "Agent Quota Tracker", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        if (_singleInstanceMutex is null) return; // Shutdown may have arrived during the asynchronous retry.
        // Hosted starts with the host (like a sign-in --startup) unless the module page asked to show it.
        var startMinimized = _hosted is not null
            ? !_hosted.ShowOnStart
            : e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase), store,
            startMinimized: startMinimized, initialSettings: settings, hosted: _hosted is not null);
        MainWindow = window;
        window.Start();
        if (_hosted is { OpenSettingsOnStart: true }) window.HandleHostCommand(HostCommand.Settings);
    }

    private static void SignalExistingInstanceShutdown(string mutexName)
    {
        try
        {
            using var shutdownEvent = EventWaitHandle.OpenExisting(GetShutdownEventName());
            shutdownEvent.Set();
            using var mutex = Mutex.OpenExisting(mutexName);
            if (mutex.WaitOne(TimeSpan.FromSeconds(15))) mutex.ReleaseMutex();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (AbandonedMutexException) { }
    }

    private static string GetSingleInstanceMutexName()
    {
        // Every executable location uses the same per-user data and OAuth refresh token.
        // A mutex per executable path allowed dev/installed copies to overwrite each other.
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return @"Local\AqTracker.User." + identity.User!.Value;
    }

    private static string GetShutdownEventName() => GetSingleInstanceMutexName() + ".Shutdown";

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        _shutdownEvent?.Dispose();
        _shutdownEvent = null;
        _commands?.Dispose();
        _commands = null;
        _hosted?.Dispose();
        _hosted = null;
        base.OnExit(e);
    }
}
