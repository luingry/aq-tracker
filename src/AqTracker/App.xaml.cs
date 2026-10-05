namespace AqTracker;
public partial class App : System.Windows.Application
{
    private readonly bool _launchMainWindow;
    public App() : this(true) { }
    // WPF schedules OnStartup even when a UI harness does not call Run().
    public App(bool launchMainWindow) => _launchMainWindow = launchMainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _shutdownEvent;

    /// <summary>
    /// Multicore JIT replays the previous run's startup JIT on spare cores, so a sign-in start
    /// competing with every other startup app compiles less on the UI thread. NGen would need
    /// elevation, which this per-user install never asks for. Any failure just means normal JIT.
    /// </summary>
    private static void StartJitProfile()
    {
        try
        {
            var directory = System.IO.Path.Combine(AqTracker.Core.UserFolders.LocalApplicationData, "AqTracker", "jit");
            System.IO.Directory.CreateDirectory(directory);
            System.Runtime.ProfileOptimization.SetProfileRoot(directory);
            System.Runtime.ProfileOptimization.StartProfile("startup.jitprofile");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException) { }
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (!_launchMainWindow) { base.OnStartup(e); return; }
        var mutexName = GetSingleInstanceMutexName();
        if (e.Args.Contains("--shutdown-existing", StringComparer.OrdinalIgnoreCase))
        {
            SignalExistingInstanceShutdown(mutexName);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, mutexName, out var firstInstance);
        if (!firstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }
        // Only the surviving instance records: a duplicate that exits at once would overwrite the profile.
        StartJitProfile();

        var shutdownEventName = GetShutdownEventName();
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shutdownEventName);
        _ = Task.Run(() =>
        {
            _shutdownEvent.WaitOne();
            Dispatcher.Invoke(Shutdown);
        });

        base.OnStartup(e);
        AqTracker.Core.SanitizedLogger.Write("Application startup: " + typeof(App).Assembly.GetName().Version + "; startup=" + e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        AqTracker.Core.LegacyDataMigration.MigrateRoamingData();
        var store = new SettingsStore();
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
        var window = new MainWindow(e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase), store,
            startMinimized: e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase), initialSettings: settings);
        MainWindow = window;
        window.Start();
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
        base.OnExit(e);
    }
}
