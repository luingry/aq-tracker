namespace AqTracker;
public partial class App : System.Windows.Application
{
    private readonly bool _launchMainWindow;
    public App() : this(true) { }
    // WPF schedules OnStartup even when a UI harness does not call Run().
    public App(bool launchMainWindow) => _launchMainWindow = launchMainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _shutdownEvent;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
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

        var shutdownEventName = GetShutdownEventName();
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, shutdownEventName);
        _ = Task.Run(() =>
        {
            _shutdownEvent.WaitOne();
            Dispatcher.Invoke(Shutdown);
        });

        base.OnStartup(e);
        AqTracker.Core.LegacyDataMigration.MigrateRoamingData();
        var window = new MainWindow(e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase),
            startMinimized: e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        window.Show();
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
