using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AqTracker.Core;
using Forms = System.Windows.Forms;
using WpfCursors = System.Windows.Input.Cursors;

namespace AqTracker;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly WindowsStartupRegistration _windowsStartup;
    private bool _startupHidden;
    private bool? _startupEngaged;
    private readonly MainViewModel _viewModel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly LocalUsageAnalyticsService _analytics = new();
    private readonly StartupAnalyticsCoordinator _startupAnalytics = new();
    private readonly AgentActivityService _agentActivity = new();
    private readonly UpdateController _updates = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _agentTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _visibilityTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, string> _threadTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _threadTitleLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _demo;
    private readonly HashSet<string> _observedCompletionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompletedAgentWork> _unreadAgentWorks;
    private AppSettings _settings;
    private CodexAppServerClient? _client;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIcon;
    private int _connecting;
    private int _analyticsRunning;
    private int _agentRefreshRunning;
    private int _titleRefreshRunning;
    private int _refreshTick;
    private int _updateCheckRunning;
    private int _updateInstallRunning;
    private bool _agentStateInitialized;
    private UsageAnalytics? _lastLoadedAnalytics;
    private UpdateAvailability? _pendingUpdate;
    private ChatDetailsWindow? _chatDetailsWindow;
    private CancellationTokenSource? _updateInstallCancellation;
    private bool _dragCandidate;
    private bool _nativeDragInProgress;
    private bool _manualResize;
    private bool _resizeGestureActive;
    private bool _suppressDoubleClickToggle;
    private string _pendingAccentColor = AccentPalette.DefaultBaseHex;
    private System.Windows.Point _dragStart;
    private System.Windows.Point _resizeStartScreen;
    private Rect _resizeStartBounds;
    private ResizeWorkArea _resizeWorkArea;
    private ResizeEdge _resizeEdge;
    // The compact XAML reserves a 42 DIP gauge inside the 62 x 52 minimum window.
    private const double CompactAspectRatio = WidgetSizePolicy.CompactAspectRatio;
    private const double CompactMinWidth = WidgetSizePolicy.CompactMinWidth;
    private const double CompactMaxWidth = WidgetSizePolicy.CompactMaxWidth;
    private const double CompactAgentIndicatorHeight = 24d;
    private const double ResizeBorderThickness = 6d;
    private const double DetailedVerticalMargin = 24d;
    private double _detailedContentMaxHeight = WidgetSizePolicy.DetailedMaxHeight;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmMoving = 0x0216;
    private const int WmExitSizeMove = 0x0232;
    private const int HtCaption = 0x0002;
    private const int GwlExStyle = -20;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [Flags]
    private enum ResizeEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    public MainWindow(bool demo = false, SettingsStore? settingsStore = null, bool startMinimized = false, WindowsStartupRegistration? windowsStartup = null)
    {
        if (settingsStore is not null) _store = settingsStore;
        _windowsStartup = windowsStartup ?? new WindowsStartupRegistration(RuntimePaths.ExecutablePath);
        _startupHidden = startMinimized;
        if (startMinimized) { ShowActivated = false; Opacity = 0; }
        _settings = _store.Load();
        _unreadAgentWorks = (_settings.UnreadAgentWorks ?? []).OrderByDescending(work => work.CompletedAt).ToList();
        _observedCompletionIds.UnionWith(_unreadAgentWorks.Select(work => work.CompletionId));
        LocalizationManager.Apply(_settings.LanguageCode);
        _viewModel = new MainViewModel(new QuotaSnapshotStore());
        InitializeClaude();
        InitializeComponent();
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnWindowPreviewMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(OnWindowPreviewMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnWindowPreviewMouseUp), true);
        LostMouseCapture += (_, _) => { if (_manualResize) FinishManualResize(false); };
        Deactivated += (_, _) => { if (_manualResize) FinishManualResize(false); };
        DataContext = _viewModel;
        _viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(MainViewModel.ShowCompactFiveHour) or nameof(MainViewModel.ShowCompactWeekly) or nameof(MainViewModel.CompactQuotaCount))
                ApplyWindowModeSize();
        };
        _demo = demo;
        _pendingAccentColor = _settings.AccentColor;
        Topmost = _settings.IsTopmost;
        ApplyProfileTheme(_settings.Theme, _settings.AccentColor, _settings.ClaudeAccentColor);
        _viewModel.Topmost = Topmost;
        _viewModel.SetCurrency(_settings.CurrencyCode);
        _viewModel.SetCompactQuotaDisplay(_settings.CompactQuotaDisplay);
        _viewModel.Expanded = _settings.IsExpanded;
        _viewModel.IsAgentListOpen = false;
        _viewModel.ApplyUnreadCompletedAgents(VisibleUnreadWorks());
        ApplyWindowModeSize();
        Left = _settings.Left;
        Top = _settings.Top;
        Loaded += async (_, _) =>
        {
            if (_startupHidden) { Hide(); Opacity = 1; }
            _refreshTimer.Start();
            _agentTimer.Start();
            _visibilityTimer.Start();
            _ = LoadAsync();
            _ = RefreshClaudeAsync();
            await RefreshAgentsAsync();
            UpdateWidgetVisibility();
            _ = RefreshAnalyticsAsync();
            _ = CheckForUpdatesIfDueAsync();
        };
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => RepositionAgentListPopup();
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _agentTimer.Tick += async (_, _) => await RefreshAgentsAsync();
        _visibilityTimer.Tick += (_, _) => UpdateWidgetVisibility();
        CreateTray();
    }

    private async Task LoadAsync()
    {
        if (_demo)
        {
            _viewModel.Apply(new RateLimitSnapshot([new("codex:primary", "5-hour limit", 16, DateTimeOffset.Now.AddHours(3), 300), new("codex:secondary", "Weekly limit", 42, DateTimeOffset.Now.AddDays(3), 10080)], "demo", "Credits: unlimited", "Reset credits: 2", DateTimeOffset.UtcNow), new(12400, 240300, 11.4m, 16, 100, [new("gpt-5.6-terra", 240300, 11.4m, true)]), _settings.CurrencyCode);
            ApplyWindowModeSize();
            return;
        }
        if (Interlocked.Exchange(ref _connecting, 1) == 1) return;
        var connectionGeneration = _startupAnalytics.BeginConnection();
        try
        {
            if (_client is { } previousClient)
            {
                _client = null;
                await previousClient.DisposeAsync();
            }
            var executable = await Task.Run(
                () => CodexExecutableDiscovery.Find(_settings.CodexPath),
                _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();
            if (executable is null) { _viewModel.Status = LocalizationManager.Text("CodexNotFound"); SanitizedLogger.Write("Discovery failed"); return; }
            SanitizedLogger.Write("App-server starting: " + executable);
            var client = new CodexAppServerClient(executable);
            Action<string> statusChanged = status =>
            {
                if (!_startupAnalytics.IsCurrent(connectionGeneration)) return;
                Dispatcher.Invoke(() =>
                {
                    if (!_startupAnalytics.IsCurrent(connectionGeneration)) return;
                    _viewModel.Status = status.StartsWith("Connection interrupted", StringComparison.Ordinal)
                        ? LocalizationManager.Text("StaleReconnecting")
                        : status == "Connecting to Codex" ? LocalizationManager.Text("ConnectingToCodex")
                        : status == "Live from Codex" ? LocalizationManager.Text("LiveData")
                        : status;
                });
            };
            Action<RateLimitSnapshot> snapshotUpdated = snapshot =>
            {
                var pendingUsage = _startupAnalytics.OnSnapshot(snapshot, _viewModel.Expanded, connectionGeneration);
                if (!_startupAnalytics.IsCurrent(connectionGeneration)) return;
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (!_startupAnalytics.IsCurrent(connectionGeneration)) return;
                    _viewModel.ApplyQuota(snapshot);
                    ApplyWindowModeSize();
                    if (pendingUsage is not null && _viewModel.Expanded)
                        _viewModel.Apply(snapshot, pendingUsage, _settings.CurrencyCode);
                    else
                        ApplyCachedAnalyticsIfDetailed(snapshot);
                });
                var weekly = OfficialCodexQuotaWindows.Weekly(snapshot.Windows);
                var fiveHour = OfficialCodexQuotaWindows.FiveHours(snapshot.Windows);
                SanitizedLogger.Write($"Quota snapshot summary: windows={snapshot.Windows.Count}, 5h={(fiveHour is null ? "missing" : fiveHour.UsedPercent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}, weekly={(weekly is null ? "missing" : weekly.UsedPercent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))}");
            };
            client.StatusChanged += statusChanged;
            client.SnapshotUpdated += snapshotUpdated;
            _client = client;
            try
            {
                await client.StartAsync(_shutdown.Token);
            }
            catch
            {
                client.StatusChanged -= statusChanged;
                client.SnapshotUpdated -= snapshotUpdated;
                if (ReferenceEquals(_client, client)) _client = null;
                try { await client.DisposeAsync(); }
                catch { }
                throw;
            }
            SanitizedLogger.Write("App-server initialized and read");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) { _viewModel.Status = LocalizationManager.Text("ConnectionError"); SanitizedLogger.Write("Connect error: " + exception.GetType().Name); }
        finally { Volatile.Write(ref _connecting, 0); }
    }

    private async Task RefreshAsync()
    {
        _ = CheckForUpdatesIfDueAsync();
        _ = RefreshClaudeAsync();
        if (_client is null) { await LoadAsync(); return; }
        try
        {
            await _client.RefreshAsync(_shutdown.Token);
            if (Interlocked.Increment(ref _refreshTick) % 5 == 0) _ = RefreshAnalyticsAsync();
        }
        catch (Exception exception) { _viewModel.Status = LocalizationManager.Text("StaleRetrying"); SanitizedLogger.Write("Refresh error: " + exception.GetType().Name); await LoadAsync(); }
    }

    private async Task RefreshAnalyticsAsync()
    {
        if (Interlocked.Exchange(ref _analyticsRunning, 1) == 1) return;
        try
        {
            var usage = await _analytics.ReadAsync(_settings.UsdBrl);
            _lastLoadedAnalytics = usage;
            var ready = _startupAnalytics.OnAnalyticsReady(usage, _viewModel.Expanded);
            if (ready is { } application)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_viewModel.Expanded)
                        _viewModel.Apply(application.Snapshot, application.Usage, _settings.CurrencyCode);
                });
        }
        catch (Exception exception)
        {
            SanitizedLogger.Write("Analytics refresh error: " + exception.GetType().Name);
        }
        finally { Volatile.Write(ref _analyticsRunning, 0); }
    }

    private void ApplyCachedAnalyticsIfDetailed(RateLimitSnapshot snapshot)
    {
        if (!_viewModel.Expanded || _lastLoadedAnalytics is not { } usage || usage.UsdBrl != _settings.UsdBrl) return;
        _viewModel.Apply(snapshot, usage, _settings.CurrencyCode);
    }

    private async Task RefreshAgentsAsync()
    {
        if (_demo || Interlocked.Exchange(ref _agentRefreshRunning, 1) == 1) return;
        try
        {
            var titles = new Dictionary<string, string>(_threadTitles, StringComparer.OrdinalIgnoreCase);
            var activityTask = Task.Run(() => _agentActivity.ReadSnapshot(titles));
            var claudeTask = Task.Run(() => _claudeActivity.ReadSnapshot());
            await Task.WhenAll(activityTask, claudeTask);
            var claude = claudeTask.Result;
            var activity = activityTask.Result;
            var agents = activity.ActiveAgents.Concat(_settings.ClaudeProfileEnabled ? claude.ActiveAgents : []).ToArray();
            var unreadChanged = false;
            if (!_agentStateInitialized)
            {
                _observedCompletionIds.UnionWith(activity.CompletedAgentWorks.Select(work => work.CompletionId));
                _agentStateInitialized = true;
            }
            else
            {
                foreach (var completed in activity.CompletedAgentWorks.Where(work => _observedCompletionIds.Add(work.CompletionId)))
                {
                    _unreadAgentWorks.Add(completed);
                    unreadChanged = true;
                }
            }
            foreach (var completed in claude.CompletedAgentWorks)
            {
                if (!_observedCompletionIds.Add(completed.CompletionId) || !_settings.ClaudeProfileEnabled) continue;
                _unreadAgentWorks.Add(completed);
                unreadChanged = true;
            }
            var activeThreadIds = agents.Where(agent => agent.Provider == AgentProvider.Codex).Select(agent => agent.ThreadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_unreadAgentWorks.RemoveAll(work => work.Provider == AgentProvider.Codex && activeThreadIds.Contains(work.ThreadId) ||
                ClaudeSessionActivityService.ShouldRemoveCompletion(work, claude.ActiveAgents, _claudeActivity.LastFocusedAt)) > 0) unreadChanged = true;
            if (unreadChanged) PersistUnreadAgentWorks();
            var previouslyVisibleIndicator = _viewModel.HasAgentIndicator;
            var previouslyActive = _viewModel.HasActiveAgents;
            _viewModel.ApplyAgents(agents, DateTimeOffset.UtcNow, AgentListPopup.IsOpen);
            _viewModel.ApplyUnreadCompletedAgents(VisibleUnreadWorks());
            if (previouslyVisibleIndicator != _viewModel.HasAgentIndicator)
            {
                if (!_viewModel.HasAgentIndicator) _viewModel.IsAgentListOpen = false;
                else if (!_startupHidden && _settings.IsAgentListExpanded && !_viewModel.Expanded) _viewModel.IsAgentListOpen = true;
                ApplyCompactAgentIndicatorSize();
            }
            else if (!_startupHidden && previouslyActive != _viewModel.HasActiveAgents && _settings.IsAgentListExpanded && !_viewModel.Expanded)
                _viewModel.IsAgentListOpen = true;
            if (AgentListPopup.IsOpen && _viewModel.ActiveAgents.Any(row => row.IsNew))
                _ = Dispatcher.InvokeAsync(async () => { await Task.Delay(210); _viewModel.MarkNewAgentRowsStable(); });

            var now = DateTimeOffset.UtcNow;
            var missingTitles = agents.Where(agent => agent.Provider == AgentProvider.Codex).Select(agent => agent.ThreadId)
                .Concat(_unreadAgentWorks.Where(work => work.Provider == AgentProvider.Codex).Select(work => work.ThreadId))
                .Where(id => !_threadTitles.ContainsKey(id) &&
                             (!_threadTitleLookups.TryGetValue(id, out var attemptedAt) || now - attemptedAt >= TimeSpan.FromMinutes(1)))
                .ToArray();
            if (missingTitles.Length > 0) _ = RefreshAgentTitlesAsync(missingTitles);
            UpdateWidgetVisibility();
        }
        catch (Exception exception) { SanitizedLogger.Write("Agent activity refresh error: " + exception.GetType().Name); }
        finally { Volatile.Write(ref _agentRefreshRunning, 0); }
    }

    private async Task RefreshAgentTitlesAsync(IReadOnlyList<string> threadIds)
    {
        if (_client is null || Interlocked.Exchange(ref _titleRefreshRunning, 1) == 1) return;
        try
        {
            var attemptedAt = DateTimeOffset.UtcNow;
            foreach (var threadId in threadIds) _threadTitleLookups[threadId] = attemptedAt;
            var titles = await _client.ReadThreadTitlesAsync(threadIds, _shutdown.Token);
            foreach (var title in titles) _threadTitles[title.Key] = title.Value;
            _viewModel.ApplyAgentTitles(titles);
            var unreadChanged = false;
            for (var index = 0; index < _unreadAgentWorks.Count; index++)
            {
                var work = _unreadAgentWorks[index];
                if (work.Provider != AgentProvider.Codex) continue;
                if (!titles.TryGetValue(work.ThreadId, out var title) || string.IsNullOrWhiteSpace(title) || string.Equals(work.Title, title, StringComparison.Ordinal)) continue;
                _unreadAgentWorks[index] = work with { Title = title.Trim() };
                unreadChanged = true;
            }
            if (unreadChanged)
            {
                PersistUnreadAgentWorks();
                _viewModel.ApplyUnreadCompletedAgents(VisibleUnreadWorks());
            }
        }
        catch (Exception exception) { SanitizedLogger.Write("Agent title refresh error: " + exception.GetType().Name); }
        finally { Volatile.Write(ref _titleRefreshRunning, 0); }
    }

    private static string CurrentVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private async Task CheckForUpdatesIfDueAsync()
    {
        if (_demo || !UpdateCheckPolicy.IsDue(_settings.LastUpdateCheckUtc, DateTimeOffset.UtcNow)) return;
        await RunUpdateCheckAsync(manual: false);
    }

    private async void CheckForUpdatesManual(object sender, RoutedEventArgs e) => await RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (Interlocked.Exchange(ref _updateCheckRunning, 1) == 1)
        {
            if (manual) _viewModel.UpdateCheckFeedback = LocalizationManager.Text("UpdateCheckInProgress");
            return;
        }
        try
        {
            if (manual) _viewModel.UpdateCheckFeedback = LocalizationManager.Text("UpdateCheckChecking");
            else
            {
                // Persist the automatic attempt before touching the network. A disconnected app or
                // unavailable GitHub endpoint must not turn the 60-second refresh timer into retries.
                _settings = _settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow };
                _store.Save(_settings);
                _pendingUpdate = null;
            }
            UpdateAvailability availability;
            try
            {
                availability = await _updates.CheckAsync(CurrentVersion, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                SanitizedLogger.Write("Update check error: " + exception.GetType().Name);
                if (manual) _viewModel.UpdateCheckFeedback = LocalizationManager.Text("UpdateCheckFailed");
                return;
            }
            if (manual)
            {
                _settings = _settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow };
                _store.Save(_settings);
            }
            if (!availability.IsAvailable || availability.LatestVersion is null)
            {
                if (manual) _viewModel.UpdateCheckFeedback = LocalizationManager.Text("UpdateCheckUpToDate");
                return;
            }
            _pendingUpdate = availability;
            if (manual)
            {
                _viewModel.UpdateCheckFeedback = "";
                ShowUpdateDialog(availability);
            }
            else if (_viewModel.Expanded) MaybeShowPendingUpdateDialog();
        }
        finally { Volatile.Write(ref _updateCheckRunning, 0); }
    }

    private void MaybeShowPendingUpdateDialog()
    {
        if (_pendingUpdate is not { IsAvailable: true, LatestVersion: { } version } availability) return;
        if (_viewModel.IsUpdateDialogOpen) return;
        if (UpdateDeferralPolicy.ShouldSuppressAutomaticPrompt(_settings.DeferredUpdateVersion, _settings.UpdateDeferredAtUtc, version, _settings.LastUpdateCheckUtc)) return;
        ShowUpdateDialog(availability);
    }

    private void ShowUpdateDialog(UpdateAvailability availability)
    {
        if (_viewModel.IsUpdateDialogOpen) return;
        UpdateLaterButton.IsEnabled = true;
        UpdateNowButton.IsEnabled = true;
        _viewModel.UpdateAvailableVersion = availability.LatestVersion ?? "";
        _viewModel.UpdateStatusMessage = "";
        _viewModel.UpdateProgress = 0;
        _viewModel.IsUpdateDownloading = false;
        _viewModel.IsUpdateDialogOpen = true;
    }

    private async void StartUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { IsAvailable: true, DownloadUrl: { } url } || Interlocked.Exchange(ref _updateInstallRunning, 1) == 1) return;
        UpdateLaterButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
        _viewModel.IsUpdateDownloading = true;
        _viewModel.UpdateProgress = 0;
        _viewModel.UpdateStatusMessage = LocalizationManager.Text("UpdateDownloading");
        _updateInstallCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(value =>
            {
                _viewModel.UpdateProgress = value;
                _viewModel.UpdateStatusMessage = LocalizationManager.Format("UpdateDownloadingPercent", (int)Math.Round(value * 100));
            });
            var installerPath = await _updates.DownloadAsync(url, progress, _updateInstallCancellation.Token);
            _viewModel.UpdateStatusMessage = LocalizationManager.Text("UpdateInstalling");
            _updates.StartSilentInstall(installerPath);
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            SanitizedLogger.Write("Update install error: " + exception.GetType().Name);
            _viewModel.UpdateStatusMessage = LocalizationManager.Text("UpdateFailed");
            _viewModel.IsUpdateDownloading = false;
            UpdateLaterButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = true;
        }
        finally
        {
            _updateInstallCancellation?.Dispose();
            _updateInstallCancellation = null;
            Volatile.Write(ref _updateInstallRunning, 0);
        }
    }

    private void DeferUpdate(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _updateInstallRunning) == 1) return;
        if (_pendingUpdate is { LatestVersion: { } version })
        {
            _settings = _settings with { DeferredUpdateVersion = version, UpdateDeferredAtUtc = DateTimeOffset.UtcNow };
            _store.Save(_settings);
        }
        _viewModel.IsUpdateDialogOpen = false;
    }

    private void CreateTray()
    {
        _tray ??= new Forms.NotifyIcon { Visible = true, Text = "Agent Quota Tracker" };
        var previousIcon = _trayIcon;
        _trayIcon = CreateTrayIcon(_settings.AccentColor);
        _tray.Icon = _trayIcon;
        previousIcon?.Dispose();

        var previousMenu = _trayMenu;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(LocalizationManager.Text("Show"), null, (_, _) => ShowFromTray());
        menu.Items.Add(LocalizationManager.Text("ToggleDetailedMode"), null, (_, _) => Dispatcher.Invoke(() => ToggleDetailed(this, new RoutedEventArgs())));
        menu.Items.Add(LocalizationManager.Text("Refresh"), null, async (_, _) => await RefreshAsync());
        menu.Items.Add(LocalizationManager.Text("Exit"), null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _trayMenu = menu;
        previousMenu?.Dispose();
    }

    private void ShowFromTray()
    {
        _startupHidden = false;
        Opacity = 1;
        Show();
        Activate();
    }

    private static System.Drawing.Icon CreateTrayIcon(string? accentColor)
    {
        if (RuntimePaths.ExecutablePath is { } executable && System.Drawing.Icon.ExtractAssociatedIcon(executable) is { } applicationIcon)
        {
            using (applicationIcon) return (System.Drawing.Icon)applicationIcon.Clone();
        }

        using var bitmap = new Bitmap(32, 32);
        var fallbackColor = ColorTranslator.FromHtml(AccentPalette.Normalize(accentColor));
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(fallbackColor)) graphics.FillEllipse(brush, 4, 4, 24, 24);
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private void ToggleAgentList(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.HasAgentIndicator) return;
        var isOpen = !_viewModel.IsAgentListOpen;
        _viewModel.IsAgentListOpen = isOpen;
        _settings = _settings with { IsAgentListExpanded = isOpen };
        Save();
        RepositionAgentListPopup();
    }

    private void OpenChatDetails(object sender, RoutedEventArgs e)
    {
        if (_chatDetailsWindow is { IsVisible: true })
        {
            _chatDetailsWindow.Activate();
            return;
        }
        _viewModel.ResetChatDetailsView();
        var window = new ChatDetailsWindow(_viewModel) { Owner = this };
        _chatDetailsWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_chatDetailsWindow, window)) _chatDetailsWindow = null; };
        window.Show();
    }

    private void OpenAgentThread(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { DataContext: AgentActivityRow { Provider: AgentProvider.Claude } claudeRow })
        {
            if (claudeRow.IsCompleted) MarkThreadRead(claudeRow.ThreadId, AgentProvider.Claude);
            if (ClaudeSessionDeepLink.TryCreate(claudeRow.HostSessionId, out var claudeLink) && claudeLink is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(claudeLink.AbsoluteUri) { UseShellExecute = true });
                    return;
                }
                catch (Exception exception) { SanitizedLogger.Write("Claude session link launch failed: " + exception.GetType().Name); }
            }
            CodexDesktopWindowMonitor.BringClaudeToFront();
            return;
        }
        if (sender is not System.Windows.Controls.Button { CommandParameter: string threadId } || !CodexThreadDeepLink.TryCreate(threadId, out var deepLink) || deepLink is null)
        {
            SanitizedLogger.Write("Agent thread link rejected");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(deepLink.AbsoluteUri) { UseShellExecute = true });
            if (sender is System.Windows.Controls.Button { DataContext: AgentActivityRow { IsCompleted: true } }) MarkThreadRead(threadId);
        }
        catch (Exception exception) { SanitizedLogger.Write("Agent thread link launch failed: " + exception.GetType().Name); }
    }

    private void MarkThreadRead(string threadId, AgentProvider provider = AgentProvider.Codex)
    {
        if (_unreadAgentWorks.RemoveAll(work => work.Provider == provider && string.Equals(work.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)) == 0) return;
        PersistUnreadAgentWorks();
        var previouslyVisibleIndicator = _viewModel.HasAgentIndicator;
        _viewModel.ApplyUnreadCompletedAgents(VisibleUnreadWorks());
        if (previouslyVisibleIndicator != _viewModel.HasAgentIndicator) ApplyCompactAgentIndicatorSize();
        if (!_viewModel.HasAgentIndicator) _viewModel.IsAgentListOpen = false;
    }

    private void MarkAllCompletedAgentsRead(object sender, RoutedEventArgs e)
    {
        if (_unreadAgentWorks.Count == 0) return;
        _unreadAgentWorks.Clear();
        PersistUnreadAgentWorks();
        var previouslyVisibleIndicator = _viewModel.HasAgentIndicator;
        _viewModel.MarkAllCompletedAgentsRead();
        if (previouslyVisibleIndicator != _viewModel.HasAgentIndicator) ApplyCompactAgentIndicatorSize();
        if (!_viewModel.HasAgentIndicator) _viewModel.IsAgentListOpen = false;
        UpdateWidgetVisibility();
    }

    private void PersistUnreadAgentWorks()
    {
        _unreadAgentWorks.Sort((left, right) => right.CompletedAt.CompareTo(left.CompletedAt));
        if (_unreadAgentWorks.Count > 50) _unreadAgentWorks.RemoveRange(50, _unreadAgentWorks.Count - 50);
        _settings = _settings with { UnreadAgentWorks = _unreadAgentWorks.ToArray() };
        _store.Save(_settings);
    }

    private void UpdateWidgetVisibility()
    {
        if (_demo || !IsLoaded) return;
        var desktop = CodexDesktopWindowMonitor.ReadForeground();
        var codex = new ProfileActivity(desktop.Provider == AgentProvider.Codex, desktop.IsMinimized,
            _viewModel.ActiveAgents.Any(row => row.Provider == AgentProvider.Codex),
            _unreadAgentWorks.Any(work => work.Provider == AgentProvider.Codex));
        var claude = _settings.ClaudeProfileEnabled ? new ProfileActivity(desktop.Provider == AgentProvider.Claude, desktop.IsMinimized,
            _viewModel.ActiveAgents.Any(row => row.Provider == AgentProvider.Claude),
            _unreadAgentWorks.Any(work => work.Provider == AgentProvider.Claude)) : default;
        _viewModel.SetProfileActivity(codex, claude);
        if (claude.IsEngaged && !_wasClaudeEngaged) _ = RefreshClaudeAsync(onlyIfStale: true);
        _wasClaudeEngaged = claude.IsEngaged;
        var shouldShow = WidgetVisibilityPolicy.ShouldShow(
            codex, claude,
            IsActive || AgentListPopup.IsOpen);
        // Do not reopen immediately for work already present at sign-in. Resume normal
        // visibility on the next idle -> engaged transition, or explicitly from the tray.
        if (_startupHidden)
        {
            // The first agent snapshot belongs to startup, even when it arrives after
            // the first visibility timer tick. Do not misclassify it as newly started work.
            if (!_agentStateInitialized) return;
            var engaged = codex.IsEngaged || claude.IsEngaged;
            if (_startupEngaged is null) { _startupEngaged = engaged; return; }
            if (!engaged) _startupEngaged = false;
            if (_startupEngaged == true || !engaged) return;
            _startupHidden = false;
        }
        if (shouldShow)
        {
            if (!IsVisible) Show();
            return;
        }
        if (!IsVisible) return;
        _viewModel.IsAgentListOpen = false;
        Hide();
    }

    private void AgentRowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => AnimateAgentRowHover(sender as System.Windows.Controls.Button, true);

    private void AgentRowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => AnimateAgentRowHover(sender as System.Windows.Controls.Button, false);

    private static void AnimateAgentRowHover(System.Windows.Controls.Button? row, bool isHovered)
    {
        if (row?.Template.FindName("AgentRowHover", row) is not Border hover) return;

        const double targetOpacity = .09;
        if (!SystemParameters.ClientAreaAnimation)
        {
            hover.BeginAnimation(OpacityProperty, null);
            hover.Opacity = isHovered ? targetOpacity : 0;
            return;
        }

        hover.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(isHovered ? targetOpacity : 0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = CreateEaseOut()
        });
    }

    private void AgentRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || sender is not System.Windows.Controls.Button row ||
            row.Template.FindName("AgentRowSurface", row) is not Border surface ||
            row.Template.FindName("AgentRowRipple", row) is not System.Windows.Shapes.Ellipse ripple ||
            row.Template.FindName("AgentRowRippleScale", row) is not System.Windows.Media.ScaleTransform rippleScale) return;

        var origin = e.GetPosition(surface);
        var furthestHorizontal = Math.Max(origin.X, Math.Max(0, surface.ActualWidth - origin.X));
        var furthestVertical = Math.Max(origin.Y, Math.Max(0, surface.ActualHeight - origin.Y));
        var scale = Math.Max(1d, Math.Sqrt(furthestHorizontal * furthestHorizontal + furthestVertical * furthestVertical) + 1d);
        Canvas.SetLeft(ripple, origin.X - 1d);
        Canvas.SetTop(ripple, origin.Y - 1d);

        ripple.BeginAnimation(OpacityProperty, null);
        rippleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        rippleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        ripple.Opacity = .30;
        rippleScale.ScaleX = rippleScale.ScaleY = 0;

        var duration = TimeSpan.FromMilliseconds(600);
        ripple.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, duration));
        rippleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(scale, duration) { EasingFunction = CreateEaseOut() });
        rippleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(scale, duration) { EasingFunction = CreateEaseOut() });
    }

    private void AgentListPopupOpened(object sender, EventArgs e)
    {
        RepositionAgentListPopup();
        if (!SystemParameters.ClientAreaAnimation) return;
        AgentListWrapper.Opacity = 0;
        AgentListWrapperScale.ScaleX = AgentListWrapperScale.ScaleY = .96;
        AgentListWrapperTranslate.Y = 4;
        var duration = TimeSpan.FromMilliseconds(200);
        AgentListWrapper.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, duration) { EasingFunction = CreateEaseOut() });
        AgentListWrapperScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, duration) { EasingFunction = CreateEaseOut() });
        AgentListWrapperScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, duration) { EasingFunction = CreateEaseOut() });
        AgentListWrapperTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, duration) { EasingFunction = CreateEaseOut() });
    }

    private static System.Windows.Media.Animation.CubicEase CreateEaseOut() => new() { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

    private void RepositionAgentListPopup()
    {
        if (!AgentListPopup.IsOpen) return;

        // Popup has no public reposition API on the target WPF runtime. Changing an
        // offset invokes its internal reposition path; restore it immediately so the
        // separate popup HWND follows native dragging without a visible displacement.
        var horizontalOffset = AgentListPopup.HorizontalOffset;
        AgentListPopup.HorizontalOffset = horizontalOffset + 0.01d;
        AgentListPopup.HorizontalOffset = horizontalOffset;
    }

    private void ToggleDetailed(object sender, RoutedEventArgs e)
    {
        CaptureCurrentModeSize();
        _viewModel.Expanded = !_viewModel.Expanded;
        if (_viewModel.Expanded) _viewModel.IsAgentListOpen = false;
        else if (_settings.IsAgentListExpanded && _viewModel.HasAgentIndicator) _viewModel.IsAgentListOpen = true;
        ApplyWindowModeSize();
        if (!_viewModel.Expanded && _viewModel.IsAgentListOpen) RepositionAgentListPopup();
        Save();
        if (_viewModel.Expanded)
        {
            if (_client?.Snapshot is { } snapshot) ApplyCachedAnalyticsIfDetailed(snapshot);
            _ = RefreshAnalyticsAsync();
            MaybeShowPendingUpdateDialog();
        }
    }
    private void Settings(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            CaptureCurrentModeSize();
            SettingsPanel.Visibility = Visibility.Collapsed;
            _pendingAccentColor = _settings.AccentColor;
            _pendingClaudeAccentColor = _settings.ClaudeAccentColor;
            LocalizationManager.Apply(_settings.LanguageCode);
            _viewModel.RefreshLocalization();
            CreateTray();
            CancelClaudeLogin();
            ApplyProfileTheme(_settings.Theme, _settings.AccentColor, _settings.ClaudeAccentColor);
            ApplyBackdrop(_settings.Theme);
            ApplyWindowModeSize();
            Save();

            return;
        }

        _viewModel.UpdateCheckFeedback = "";
        ClaudeLoginFeedback.Text = "";
        _pendingAccentColor = _settings.AccentColor;
        _pendingClaudeAccentColor = _settings.ClaudeAccentColor;
        DetailedBox.IsChecked = _viewModel.Expanded;
        StartupBox.IsChecked = _windowsStartup.IsEnabled;
        TopmostBox.IsChecked = Topmost;
        ThemeToggle.IsChecked = _settings.Theme == "Escuro";
        LanguageBox.SelectedIndex = LocalizationManager.NormalizeLanguage(_settings.LanguageCode) == "en-US" ? 1 : 0;
        _pendingAccentColor = _settings.AccentColor;
        _pendingClaudeAccentColor = _settings.ClaudeAccentColor;
        ClaudeEnabledBox.IsChecked = _settings.ClaudeProfileEnabled;
        UpdateClaudeAccentPreview();
        UpdateAccentPreview();
        CurrencyBox.SelectedIndex = SettingsStore.NormalizeCurrency(_settings.CurrencyCode) == "USD" ? 1 : 0;
        CompactQuotaBox.SelectedIndex = _settings.CompactQuotaDisplay == "5h" ? 0 : _settings.CompactQuotaDisplay == "7d" ? 1 : 2;
        UpdateCurrencyRateVisibility();
        RateBox.Text = _settings.UsdBrl.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var automaticallyDetectedPath = CodexExecutableDiscovery.Find(null);
        CodexPathFallbackPanel.Visibility = automaticallyDetectedPath is null ? Visibility.Visible : Visibility.Collapsed;
        PathBox.Text = automaticallyDetectedPath ?? _settings.CodexPath ?? "";
        CaptureCurrentModeSize();
        SettingsPanel.Visibility = Visibility.Visible;
        ApplyWindowModeSize();
    }
    private void CurrencyChanged(object sender, SelectionChangedEventArgs e) => UpdateCurrencyRateVisibility();
    private void UpdateCurrencyRateVisibility() => RatePanel.Visibility = CurrencyBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void PreviewLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not ComboBoxItem item) return;
        LocalizationManager.Apply(item.Tag as string);
        _viewModel.RefreshLocalization();
        CreateTray();
        DailyChart.InvalidateVisual();
    }
    private void ChooseAccentColor(object sender, RoutedEventArgs e)
    {
        var current = ColorTranslator.FromHtml(AccentPalette.Normalize(_pendingAccentColor));
        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = current
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _pendingAccentColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateAccentPreview();
        PreviewTheme(sender, e);
    }
    private void UpdateAccentPreview()
    {
        var normalized = AccentPalette.Normalize(_pendingAccentColor);
        AccentColorValue.Text = normalized;
        AccentColorSwatch.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalized));
    }
    private void Browse(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog(); if (dialog.ShowDialog() == true) PathBox.Text = dialog.FileName; }
    private void OpenLog(object sender, RoutedEventArgs e) { Directory.CreateDirectory(Path.GetDirectoryName(SanitizedLogger.LogPath)!); Process.Start(new ProcessStartInfo("notepad.exe", SanitizedLogger.LogPath) { UseShellExecute = true }); }
    private void ApplySettings(object sender, RoutedEventArgs e)
    {
        CaptureCurrentModeSize();
        decimal.TryParse(RateBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rate);
        var theme = ThemeToggle.IsChecked == true ? "Escuro" : "Claro";
        var currency = SettingsStore.NormalizeCurrency((CurrencyBox.SelectedItem as ComboBoxItem)?.Tag as string);
        var compactQuotaDisplay = SettingsStore.NormalizeCompactQuotaDisplay((CompactQuotaBox.SelectedItem as ComboBoxItem)?.Tag as string);
        var language = LocalizationManager.NormalizeLanguage((LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string);
        var manualCodexPath = CodexPathFallbackPanel.Visibility == Visibility.Visible
            ? string.IsNullOrWhiteSpace(PathBox.Text) ? null : PathBox.Text
            : _settings.CodexPath;
        CancelClaudeLogin();
        var startupEnabled = StartupBox.IsChecked == true;
        _windowsStartup.SetEnabled(startupEnabled);
        _settings = _settings with { CodexPath = manualCodexPath, UsdBrl = rate > 0 ? rate : 5.5m, Theme = theme, CurrencyCode = currency, AccentColor = AccentPalette.Normalize(_pendingAccentColor), LanguageCode = language, CompactQuotaDisplay = compactQuotaDisplay,
            ClaudeProfileEnabled = ClaudeEnabledBox.IsChecked != false, ClaudeAccentColor = AccentPalette.Normalize(_pendingClaudeAccentColor), StartMinimizedWithWindows = startupEnabled };
        LocalizationManager.Apply(language);
        ApplyProfileTheme(theme, _settings.AccentColor, _settings.ClaudeAccentColor);
        CreateTray();
        ApplyBackdrop(theme);
        _viewModel.SetCurrency(currency);
        _viewModel.SetCompactQuotaDisplay(compactQuotaDisplay);
        _viewModel.SetClaudeEnabled(_settings.ClaudeProfileEnabled);
        _viewModel.ApplyUnreadCompletedAgents(VisibleUnreadWorks());
        _ = RefreshAgentsAsync();
        _ = RefreshClaudeAsync();
        _viewModel.RefreshLocalization();
        _viewModel.Expanded = DetailedBox.IsChecked == true;
        Topmost = TopmostBox.IsChecked == true;
        SettingsPanel.Visibility = Visibility.Collapsed;
        ApplyWindowModeSize();
        Save();
        if (_viewModel.Expanded)
        {
            if (_client?.Snapshot is { } snapshot) ApplyCachedAnalyticsIfDetailed(snapshot);
            _ = RefreshAnalyticsAsync();
        }
        _ = LoadAsync();
    }
    private void PreviewTheme(object sender, RoutedEventArgs e)
    {
        var theme = ThemeToggle.IsChecked == true ? "Escuro" : "Claro";
        ApplyProfileTheme(theme, _pendingAccentColor, _pendingClaudeAccentColor);
        ApplyBackdrop(theme);
    }
    private void ApplyBackdrop(string theme)
    {
        ApplyWindowSurface();
        Backdrop.Apply(this, string.Equals(theme, "Escuro", StringComparison.OrdinalIgnoreCase), CurrentVisualMode);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Hide();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        RestoreWindowPosition(handle);
        ApplyTrayOnlyWindowStyle();
        ApplyBackdrop(_settings.Theme);
    }

    private static void RestoreWindowPosition(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var currentBounds)) return;

        var workAreas = Forms.Screen.AllScreens
            .Select(screen => new WidgetScreenRect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Right, screen.WorkingArea.Bottom))
            .ToArray();
        var restored = WidgetPlacementPolicy.Restore(
            new WidgetScreenRect(currentBounds.Left, currentBounds.Top, currentBounds.Right, currentBounds.Bottom), workAreas);
        if (restored == new WidgetScreenRect(currentBounds.Left, currentBounds.Top, currentBounds.Right, currentBounds.Bottom)) return;

        SetWindowPos(handle, IntPtr.Zero, restored.Left, restored.Top, 0, 0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMoving && _nativeDragInProgress)
            RepositionAgentListPopup();

        if (message == WmExitSizeMove && _nativeDragInProgress)
        {
            _nativeDragInProgress = false;
            Save();
        }

        return IntPtr.Zero;
    }

    private void ApplyTrayOnlyWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var extendedStyle = GetExtendedWindowStyle(handle).ToInt64();
        var trayOnlyStyle = TrayOnlyWindowPolicy.ToTrayOnlyExtendedStyle(extendedStyle);
        if (trayOnlyStyle == extendedStyle) return;

        SetExtendedWindowStyle(handle, new IntPtr(trayOnlyStyle));
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }
    private void ApplyWindowSurface() => WindowSurface.Background = CurrentVisualMode switch
    {
        WidgetVisualMode.Compact => System.Windows.Media.Brushes.Transparent,
        WidgetVisualMode.Detailed => (System.Windows.Media.Brush)FindResource("DetailedSurface"),
        WidgetVisualMode.Settings => (System.Windows.Media.Brush)FindResource("SettingsSurface"),
        _ => System.Windows.Media.Brushes.Transparent
    };

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragCandidate = false;
        _resizeGestureActive = false; // A new down always begins a distinct gesture.
        var position = e.GetPosition(this);
        _resizeEdge = GetResizeEdge(position);
        if (_resizeEdge != ResizeEdge.None)
        {
            _resizeGestureActive = true;
            _resizeStartScreen = GetScreenPoint(position);
            _resizeStartBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            _resizeWorkArea = GetResizeWorkArea();
            _manualResize = CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ClickCount > 1)
        {
            // A double-click on read-only content is a mode shortcut. Keep settings and
            // controls out of it, and ignore the third click of a longer click sequence.
            if (e.ClickCount == 2 &&
                !_suppressDoubleClickToggle &&
                SettingsPanel.Visibility != Visibility.Visible &&
                e.OriginalSource is DependencyObject source &&
                !IsInteractive(source))
            {
                ToggleDetailed(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }

        _dragStart = position;
        _suppressDoubleClickToggle = SettingsPanel.Visibility == Visibility.Visible ||
                                     e.OriginalSource is DependencyObject dragSource && IsInteractive(dragSource);
        _dragCandidate = true;
    }

    private void OnWindowPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_manualResize)
        {
            if (e.LeftButton != MouseButtonState.Pressed) FinishManualResize(true);
            else ApplyManualResize(GetScreenPoint(position));
            e.Handled = true;
            return;
        }

        if (_resizeGestureActive)
        {
            if (e.LeftButton != MouseButtonState.Pressed) _resizeGestureActive = false;
            e.Handled = true;
            return;
        }

        Cursor = CursorFor(GetResizeEdge(position));
        if (!_dragCandidate) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = false;
            return;
        }

        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragCandidate = false;
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;

        ReleaseCapture();
        _nativeDragInProgress = true;
        SendMessage(windowHandle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        e.Handled = true;
    }

    private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_manualResize) FinishManualResize(true);
        _resizeGestureActive = false;
        _dragCandidate = false;
    }

    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        // The preview handlers own the gesture so child controls can still receive ordinary clicks.
    }

    private ResizeEdge GetResizeEdge(System.Windows.Point position)
    {
        if (SettingsPanel.Visibility == Visibility.Visible) return ResizeEdge.None;
        var verticalOnly = _viewModel.Expanded || SettingsPanel.Visibility == Visibility.Visible;
        if (!verticalOnly) return GetCompactResizeEdge(position);

        var edge = ResizeEdge.None;
        if (position.Y <= ResizeBorderThickness) edge |= ResizeEdge.Top;
        if (position.Y >= ActualHeight - ResizeBorderThickness) edge |= ResizeEdge.Bottom;
        return edge;
    }

    private ResizeEdge GetCompactResizeEdge(System.Windows.Point position)
    {
        foreach (var gauge in GetCompactGaugeHosts())
        {
            if (gauge.Visibility != Visibility.Visible) continue;
            var bounds = gauge.TransformToAncestor(this).TransformBounds(new Rect(0, 0, gauge.ActualWidth, gauge.ActualHeight));
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            var halfWidth = bounds.Width / 2d;
            var halfHeight = bounds.Height / 2d;
            var horizontal = (position.X - (bounds.Left + halfWidth)) / halfWidth;
            var vertical = (position.Y - (bounds.Top + halfHeight)) / halfHeight;
            var radius = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (radius < 1d - ResizeBorderThickness / Math.Min(halfWidth, halfHeight) || radius > 1.05d) continue;
            var edge = ResizeEdge.None;
            if (horizontal <= -0.45d) edge |= ResizeEdge.Left;
            if (horizontal >= 0.45d) edge |= ResizeEdge.Right;
            if (vertical <= -0.45d) edge |= ResizeEdge.Top;
            if (vertical >= 0.45d) edge |= ResizeEdge.Bottom;
            return edge;
        }
        return ResizeEdge.None;
    }

    private static System.Windows.Input.Cursor CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => WpfCursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => WpfCursors.SizeNS,
        ResizeEdge.Left | ResizeEdge.Top or ResizeEdge.Right | ResizeEdge.Bottom => WpfCursors.SizeNWSE,
        ResizeEdge.Right | ResizeEdge.Top or ResizeEdge.Left | ResizeEdge.Bottom => WpfCursors.SizeNESW,
        _ => WpfCursors.Arrow
    };

    private System.Windows.Point GetScreenPoint(System.Windows.Point clientPoint)
    {
        var device = PointToScreen(clientPoint);
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(device) ?? device;
    }

    private void ApplyManualResize(System.Windows.Point screenPoint)
    {
        if (SettingsPanel.Visibility == Visibility.Visible) return;
        var delta = new ResizeVector(screenPoint.X - _resizeStartScreen.X, screenPoint.Y - _resizeStartScreen.Y);
        var start = new ResizeBounds(_resizeStartBounds.Left, _resizeStartBounds.Top, _resizeStartBounds.Width, _resizeStartBounds.Height);
        var handle = ToResizeHandle(_resizeEdge);

        if (_viewModel.Expanded || SettingsPanel.Visibility == Visibility.Visible)
        {
            var bounds = ManualResizeGeometry.ResizeVertical(start, delta, handle, _resizeWorkArea, MinHeight, MaxHeight);
            Top = bounds.Top;
            Height = bounds.Height;
            return;
        }

        var compactBounds = ManualResizeGeometry.ResizeCompact(start, delta, handle, _resizeWorkArea, CompactWidthFloor, CompactWidthCap, CompactResizeAspectRatio, CompactResizeHeightOffset);
        Left = compactBounds.Left;
        Top = compactBounds.Top - (_viewModel.HasAgentIndicator && handle.HasFlag(ResizeHandle.Top) ? CompactAgentIndicatorHeight : 0d);
        SetCompactSize(compactBounds.Width);
    }

    private ResizeWorkArea GetResizeWorkArea()
    {
        var topLeft = PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = PointToScreen(new System.Windows.Point(ActualWidth, ActualHeight));
        var windowRect = new NativeRect
        {
            Left = (int)Math.Floor(topLeft.X), Top = (int)Math.Floor(topLeft.Y),
            Right = (int)Math.Ceiling(bottomRight.X), Bottom = (int)Math.Ceiling(bottomRight.Y)
        };
        var monitor = MonitorFromRect(ref windowRect, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
            return new ResizeWorkArea(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var workTopLeft = new System.Windows.Point(monitorInfo.Work.Left, monitorInfo.Work.Top);
        var workBottomRight = new System.Windows.Point(monitorInfo.Work.Right, monitorInfo.Work.Bottom);
        if (transform is { } deviceToDip)
        {
            workTopLeft = deviceToDip.Transform(workTopLeft);
            workBottomRight = deviceToDip.Transform(workBottomRight);
        }
        return new ResizeWorkArea(workTopLeft.X, workTopLeft.Y, workBottomRight.X - workTopLeft.X, workBottomRight.Y - workTopLeft.Y);
    }

    private static ResizeHandle ToResizeHandle(ResizeEdge edge) => (ResizeHandle)(int)edge;

    private void FinishManualResize(bool endGesture)
    {
        _manualResize = false;
        if (endGesture) _resizeGestureActive = false;
        _resizeEdge = ResizeEdge.None;
        ReleaseMouseCapture();
        Cursor = WpfCursors.Arrow;
        Save();
    }

    private static bool IsInteractive(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
            if (current is System.Windows.Controls.Primitives.ButtonBase or Selector or RangeBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.Primitives.ScrollBar or Thumb or Popup) return true;
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) => current switch
    {
        System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D => System.Windows.Media.VisualTreeHelper.GetParent(current),
        _ => LogicalTreeHelper.GetParent(current)
    };
    private void ApplyWindowModeSize()
    {
        var mode = CurrentVisualMode;
        var size = WidgetSizePolicy.SelectModeSize(_settings.ModeSizes!, mode);
        if (mode == WidgetVisualMode.Detailed)
        {
            MinWidth = MaxWidth = WidgetSizePolicy.DetailedWidth;
            Width = WidgetSizePolicy.DetailedWidth;
            MinHeight = WidgetSizePolicy.DetailedMinHeight;
            MaxHeight = _detailedContentMaxHeight;
            Height = size.Height;
        }
        else if (mode == WidgetVisualMode.Settings)
        {
            // Every caller (opening Settings, quota snapshots, profile changes) resolves to the
            // same fixed height, so the panel never opens tall and later shrinks.
            var workArea = PresentationSource.FromVisual(this) is null ? (ResizeWorkArea?)null : GetResizeWorkArea();
            var height = WidgetSizePolicy.SettingsHeightForWorkArea(workArea?.Height ?? double.NaN);
            MinWidth = MaxWidth = WidgetSizePolicy.DetailedWidth;
            Width = WidgetSizePolicy.DetailedWidth;
            MinHeight = MaxHeight = height;
            Height = height;
            if (workArea is { } area) Top = Math.Max(area.Top, Math.Min(Top, area.Top + area.Height - height));
        }
        else
        {
            MinWidth = CompactWidthFloor;
            MaxWidth = CompactWidthCap;
            MinHeight = CompactHeightForWidth(CompactWidthFloor) + ActiveCompactExtraHeight;
            MaxHeight = CompactHeightForWidth(CompactWidthCap) + ActiveCompactExtraHeight;
            SetCompactSize(CompactWidthForStoredSize(size.Width));
        }
        ApplyBackdrop(_settings.Theme);
    }
    private void DetailedContentLayoutUpdated(object? sender, EventArgs e)
    {
        if (CurrentVisualMode != WidgetVisualMode.Detailed || PresentationSource.FromVisual(this) is null) return;

        var workArea = GetResizeWorkArea();
        var contentMaxHeight = WidgetSizePolicy.DetailedHeightForContent(
            DetailedContent.DesiredSize.Height + DetailedVerticalMargin, workArea.Height);
        // A user-resized detailed window must remain at the chosen height. Only a
        // content change (for example, analytics arriving) is allowed to reclaim
        // the full safe height automatically.
        if (Math.Abs(contentMaxHeight - _detailedContentMaxHeight) < .5d) return;

        _detailedContentMaxHeight = contentMaxHeight;
        MinHeight = Math.Min(WidgetSizePolicy.DetailedMinHeight, contentMaxHeight);
        MaxHeight = contentMaxHeight;
        Height = contentMaxHeight;
        Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Top + workArea.Height - Height));
    }
    private void SetCompactSize(double width)
    {
        Width = width;
        Height = CompactHeightForWidth(width) + ActiveCompactExtraHeight;
    }
    private IEnumerable<FrameworkElement> GetCompactGaugeHosts()
    {
        foreach (var item in CompactQuotaGauges.Items)
            if (CompactQuotaGauges.ItemContainerGenerator.ContainerFromItem(item) is System.Windows.Controls.ContentPresenter presenter &&
                CompactQuotaGauges.ItemTemplate.FindName("CompactGauge", presenter) is FrameworkElement gauge)
                yield return gauge;
    }
    private bool ShowsBothCompactQuotas => _viewModel.CompactQuotaCount > 1;
    private double CompactWidthFloor => ShowsBothCompactQuotas ? CompactWidthForStoredSize(CompactMinWidth) : CompactMinWidth;
    private double CompactWidthCap => ShowsBothCompactQuotas ? CompactWidthForStoredSize(CompactMaxWidth) : CompactMaxWidth;
    private double CompactResizeHeightOffset => ShowsBothCompactQuotas ? 4d : 0d;
    private double CompactResizeAspectRatio => CompactWidthFloor / (CompactHeightForWidth(CompactWidthFloor) - CompactResizeHeightOffset);
    private double CompactHeightForWidth(double width) => StoredCompactWidth(width) / CompactAspectRatio;
    private double CompactWidthForStoredSize(double width) => ShowsBothCompactQuotas
        ? _viewModel.CompactQuotaCount * (width / CompactAspectRatio - CompactGaugeLayoutPolicy.GaugeInset + 6d)
        : width;
    private double StoredCompactWidth(double width) => ShowsBothCompactQuotas
        ? (width / _viewModel.CompactQuotaCount - 6d + CompactGaugeLayoutPolicy.GaugeInset) * CompactAspectRatio
        : width;
    private double ActiveCompactExtraHeight => _viewModel.HasAgentIndicator ? CompactAgentIndicatorHeight : 0d;
    private void ApplyCompactAgentIndicatorSize()
    {
        if (CurrentVisualMode != WidgetVisualMode.Compact) return;
        MinHeight = CompactHeightForWidth(CompactWidthFloor) + ActiveCompactExtraHeight;
        MaxHeight = CompactHeightForWidth(CompactWidthCap) + ActiveCompactExtraHeight;
        SetCompactSize(Math.Max(Width, CompactWidthFloor));
    }
    private WidgetVisualMode CurrentVisualMode => SettingsPanel.Visibility == Visibility.Visible
        ? WidgetVisualMode.Settings
        : _viewModel.Expanded ? WidgetVisualMode.Detailed : WidgetVisualMode.Compact;
    private void CaptureCurrentModeSize()
    {
        var savedWidth = CurrentVisualMode == WidgetVisualMode.Compact ? StoredCompactWidth(Width) : Width;
        _settings = _settings with { ModeSizes = WidgetSizePolicy.With(_settings.ModeSizes!, CurrentVisualMode, new(savedWidth, Height)) };
    }
    private void Save()
    {
        CaptureCurrentModeSize();
        _settings = _settings with { Left = Left, Top = Top, IsExpanded = _viewModel.Expanded, IsTopmost = Topmost };
        _store.Save(_settings);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _agentTimer.Stop();
        _visibilityTimer.Stop();
        _shutdown.Cancel();
        CancelClaudeLogin();
        _claudeClient.Dispose();
        _updateInstallCancellation?.Cancel();
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _updates.Dispose();
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
        Save();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private static IntPtr GetExtendedWindowStyle(IntPtr hWnd) => IntPtr.Size == 8
        ? GetWindowLongPtr(hWnd, GwlExStyle)
        : new IntPtr(GetWindowLong(hWnd, GwlExStyle));
    private static void SetExtendedWindowStyle(IntPtr hWnd, IntPtr style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(hWnd, GwlExStyle, style);
        else SetWindowLong(hWnd, GwlExStyle, style.ToInt32());
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    private const uint MonitorDefaultToNearest = 2;
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
