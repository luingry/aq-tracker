using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using CodexTracker.Core;
using CodexTracker;

if (args.Contains("--benchmark-analytics", StringComparer.OrdinalIgnoreCase))
{
    var benchmarkRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-benchmark-" + Guid.NewGuid());
    Directory.CreateDirectory(benchmarkRoot);
    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var sourceRoots = new[] { Path.Combine(userProfile, ".codex", "sessions"), Path.Combine(userProfile, ".codex", "archived_sessions") };
    var copiedFiles = 0;
    foreach (var sourceRoot in sourceRoots.Where(Directory.Exists))
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            CopyFileSnapshot(sourcePath, Path.Combine(benchmarkRoot, $"{copiedFiles++:D5}-{Path.GetFileName(sourcePath)}"));
        }
    }
    var samples = new Dictionary<int, List<long>> { [1] = [], [2] = [] };
    UsageAnalytics? reference = null;
    foreach (var degree in new[] { 1, 2, 2, 1, 1, 2 })
    {
        var service = new LocalUsageAnalyticsService(maxParseParallelism: degree);
        var stopwatch = Stopwatch.StartNew();
        var usage = service.Read(5.5m, benchmarkRoot);
        var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        samples[degree].Add(elapsedMilliseconds);
        if (reference is null) reference = usage;
        else if (usage.MonthTokens != reference.MonthTokens || usage.TodayTokens != reference.TodayTokens || usage.MonthUsd != reference.MonthUsd || usage.TodayUsd != reference.TodayUsd)
            throw new InvalidOperationException("Sequential and parallel analytics returned different totals during the benchmark.");
        Console.WriteLine($"cold-service degree={degree} sample={samples[degree].Count} ms={elapsedMilliseconds} files={service.FilesParsedLastRead} bytes={service.BytesReadLastRead} monthTokens={usage.MonthTokens} todayTokens={usage.TodayTokens}");
    }
    foreach (var degree in new[] { 1, 2 })
    {
        samples[degree].Sort();
        Console.WriteLine($"cold-service degree={degree} medianMs={samples[degree][samples[degree].Count / 2]} minMs={samples[degree][0]} maxMs={samples[degree][samples[degree].Count - 1]}");
    }
    Directory.Delete(benchmarkRoot, true);
    return;
}

var mainWindowSource = File.ReadAllText(FindRepositoryFile("src", "CodexTracker", "MainWindow.xaml.cs"));
var themeManagerSource = File.ReadAllText(FindRepositoryFile("src", "CodexTracker", "ThemeManager.cs"));
var loadedHandlerStart = mainWindowSource.IndexOf("Loaded += async (_, _) =>", StringComparison.Ordinal);
var loadedHandlerEnd = mainWindowSource.IndexOf("Closing += OnClosing;", loadedHandlerStart, StringComparison.Ordinal);
var loadedHandler = mainWindowSource.Substring(loadedHandlerStart, loadedHandlerEnd - loadedHandlerStart);
Assert(loadedHandler.Contains("_ = LoadAsync();", StringComparison.Ordinal) &&
       loadedHandler.Contains("await RefreshAgentsAsync();", StringComparison.Ordinal) &&
       loadedHandler.Contains("_ = RefreshAnalyticsAsync();", StringComparison.Ordinal) &&
       !loadedHandler.Contains("if (_viewModel.Expanded) _ = RefreshAnalyticsAsync();", StringComparison.Ordinal),
       "startup keeps quota, agents and local analytics independent and parallel even when the widget begins compact");
var snapshotHandlerStart = mainWindowSource.IndexOf("Action<RateLimitSnapshot> snapshotUpdated = snapshot =>", StringComparison.Ordinal);
var snapshotHandlerEnd = mainWindowSource.IndexOf("await client.StartAsync", snapshotHandlerStart, StringComparison.Ordinal);
var snapshotHandler = mainWindowSource.Substring(snapshotHandlerStart, snapshotHandlerEnd - snapshotHandlerStart);
Assert(snapshotHandler.IndexOf("_viewModel.ApplyQuota(snapshot)", StringComparison.Ordinal) >= 0 &&
       snapshotHandler.IndexOf("_startupAnalytics.OnSnapshot", StringComparison.Ordinal) >= 0 &&
       mainWindowSource.Contains("_startupAnalytics.OnAnalyticsReady", StringComparison.Ordinal),
       "the startup race joins quota and analytics without discarding whichever finishes first");
Assert(mainWindowSource.Contains("await Task.Run(", StringComparison.Ordinal) &&
       mainWindowSource.Contains("() => CodexExecutableDiscovery.Find(_settings.CodexPath)", StringComparison.Ordinal) &&
       mainWindowSource.Contains("_shutdown.Token);", StringComparison.Ordinal),
       "Codex executable discovery leaves the UI dispatcher with shutdown cancellation before app-server startup");
var discoveryAwait = mainWindowSource.IndexOf("() => CodexExecutableDiscovery.Find(_settings.CodexPath)", StringComparison.Ordinal);
var startupReset = mainWindowSource.LastIndexOf("_startupAnalytics.BeginConnection();", discoveryAwait, StringComparison.Ordinal);
var cancellationCheck = mainWindowSource.IndexOf("_shutdown.Token.ThrowIfCancellationRequested();", discoveryAwait, StringComparison.Ordinal);
var clientCreation = mainWindowSource.IndexOf("var client = new CodexAppServerClient(executable);", cancellationCheck, StringComparison.Ordinal);
Assert(startupReset >= 0 && startupReset < discoveryAwait && cancellationCheck > discoveryAwait && clientCreation > cancellationCheck,
       "startup coordination resets before asynchronous discovery, then shutdown is rechecked before app-server client creation");
Assert(mainWindowSource.Contains("client.StatusChanged -= statusChanged;", StringComparison.Ordinal) &&
       mainWindowSource.Contains("client.SnapshotUpdated -= snapshotUpdated;", StringComparison.Ordinal) &&
       mainWindowSource.Contains("if (ReferenceEquals(_client, client)) _client = null;", StringComparison.Ordinal) &&
       mainWindowSource.Contains("await client.DisposeAsync();", StringComparison.Ordinal),
       "a failed app-server start detaches callbacks, clears ownership and disposes the newly created client");
Assert(mainWindowSource.Contains("if (Interlocked.Increment(ref _refreshTick) % 5 == 0) _ = RefreshAnalyticsAsync();", StringComparison.Ordinal) && !mainWindowSource.Contains("if (_viewModel.Expanded && Interlocked.Increment(ref _refreshTick)", StringComparison.Ordinal), "the five-minute analytics refresh continues in compact mode while retaining its non-overlapping scheduling gate");
Assert(mainWindowSource.Contains("private UsageAnalytics? _lastLoadedAnalytics;", StringComparison.Ordinal) && mainWindowSource.Contains("_lastLoadedAnalytics = usage;", StringComparison.Ordinal) && mainWindowSource.Contains("private void ApplyCachedAnalyticsIfDetailed(RateLimitSnapshot snapshot)", StringComparison.Ordinal) && mainWindowSource.Contains("if (!_viewModel.Expanded || _lastLoadedAnalytics is not { } usage || usage.UsdBrl != _settings.UsdBrl) return;", StringComparison.Ordinal) && mainWindowSource.Contains("if (_client?.Snapshot is { } snapshot) ApplyCachedAnalyticsIfDetailed(snapshot);", StringComparison.Ordinal), "compact analytics stay cached without touching the compact UI, apply immediately only when their USD/BRL rate matches current settings, and otherwise wait for the scheduled fresh read");

var coordinatorNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
var coordinatorSnapshot = new RateLimitSnapshot([new("codex:primary", "Weekly", 20, coordinatorNow.AddDays(4), 10080)], null, null, null, coordinatorNow);
var coordinatorUsage = new UsageAnalytics(123, 456, 1.2m, 6.6m, 100, []);
var analyticsFirstCoordinator = new StartupAnalyticsCoordinator();
var analyticsFirstGeneration = analyticsFirstCoordinator.BeginConnection();
Assert(analyticsFirstCoordinator.OnAnalyticsReady(coordinatorUsage, true) is null, "analytics first waits for the startup quota snapshot without being discarded");
Assert(analyticsFirstCoordinator.OnSnapshot(coordinatorSnapshot, true, analyticsFirstGeneration) == coordinatorUsage && analyticsFirstCoordinator.OnSnapshot(coordinatorSnapshot, true, analyticsFirstGeneration) is null, "quota first consumes an analytics-first result exactly once");
var snapshotFirstCoordinator = new StartupAnalyticsCoordinator();
var snapshotFirstGeneration = snapshotFirstCoordinator.BeginConnection();
Assert(snapshotFirstCoordinator.OnSnapshot(coordinatorSnapshot, true, snapshotFirstGeneration) is null, "quota first has no pending analytics result to consume");
var snapshotFirstApplication = snapshotFirstCoordinator.OnAnalyticsReady(coordinatorUsage, true);
Assert(snapshotFirstApplication is { } && snapshotFirstApplication.Value.Snapshot == coordinatorSnapshot && snapshotFirstApplication.Value.Usage == coordinatorUsage, "analytics first after a quota snapshot applies immediately against that snapshot");
var reconnectPendingCoordinator = new StartupAnalyticsCoordinator();
var staleGeneration = reconnectPendingCoordinator.BeginConnection();
Assert(reconnectPendingCoordinator.OnAnalyticsReady(coordinatorUsage, true) is null, "analytics-first result is pending before reconnect");
var currentGeneration = reconnectPendingCoordinator.BeginConnection();
Assert(!reconnectPendingCoordinator.IsCurrent(staleGeneration) && reconnectPendingCoordinator.OnSnapshot(coordinatorSnapshot, true, staleGeneration) is null, "a callback from the previous app-server generation cannot repopulate the coordinator during reconnect");
Assert(reconnectPendingCoordinator.OnSnapshot(coordinatorSnapshot, true, currentGeneration) == coordinatorUsage && reconnectPendingCoordinator.OnSnapshot(coordinatorSnapshot, true, currentGeneration) is null, "reconnect preserves pending local analytics until the current snapshot consumes it once");
var reconnectSnapshotCoordinator = new StartupAnalyticsCoordinator();
var previousSnapshotGeneration = reconnectSnapshotCoordinator.BeginConnection();
_ = reconnectSnapshotCoordinator.OnSnapshot(coordinatorSnapshot, true, previousSnapshotGeneration);
var nextSnapshotGeneration = reconnectSnapshotCoordinator.BeginConnection();
Assert(reconnectSnapshotCoordinator.OnAnalyticsReady(coordinatorUsage, true) is null, "analytics after reconnect never applies against the old snapshot");
Assert(reconnectSnapshotCoordinator.OnSnapshot(coordinatorSnapshot, true, nextSnapshotGeneration) == coordinatorUsage, "analytics after reconnect waits for and applies against the new snapshot");
var quotaSeriesNow = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.FromHours(-3));
var dailyQuota = DailyQuotaSeries.CloseByLocalDay([
    new(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero), 20),
    new(new DateTimeOffset(2026, 8, 10, 21, 0, 0, TimeSpan.Zero), 12),
    new(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero), 40)
], new DateTime(2026, 8, 1), quotaSeriesNow, 55);
Assert(dailyQuota.Single(x => x.Day.Day == 10).UsedPercent == 12 && dailyQuota.Single(x => x.Day.Day == 11).UsedPercent is null && dailyQuota.Single(x => x.Day.Day == 12).UsedPercent == 55 && dailyQuota.Single(x => x.Day.Day == 13).UsedPercent is null, "daily quota closes each local day chronologically, preserves missing days, uses live today, and never forecasts future days");
Assert(QuotaCurve.RemainingPercent(0) == 100 && QuotaCurve.RemainingPercent(100) == 0 && QuotaCurve.RemainingPercent(25) == 75 && QuotaCurve.RemainingPercent(null) is null, "daily chart converts used quota to remaining quota once while preserving unknown readings");
var quotaCurvePoints = QuotaCurve.Segments([
    new System.Windows.Point(2, 90), null, new System.Windows.Point(14, 50), null, new System.Windows.Point(23, 75)
]).Single();
Assert(quotaCurvePoints.Count == 3 && quotaCurvePoints[1].X == 14 && quotaCurvePoints[2].X == 23, "quota curve connects known readings across unknown days without compacting their calendar X positions or projecting endpoints");
var quotaStorePath = Path.Combine(Path.GetTempPath(), "codex-tracker-quota-store-" + Guid.NewGuid() + ".json");
var quotaStore = new QuotaSnapshotStore(quotaStorePath);
_ = quotaStore.Append(quotaSeriesNow, 44);
Assert(new QuotaSnapshotStore(quotaStorePath).Read().Single() == new TimedQuotaUsage(quotaSeriesNow, 44), "official live quota readings persist independently of raw rollouts");
var initialQuotaNow = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.FromHours(-3));
var initialStore = new QuotaSnapshotStore(persistent: false);
Assert(initialStore.CaptureInitial(initialQuotaNow, 25, initialQuotaNow).Single().UsedPercent == 25 && initialStore.CaptureInitial(initialQuotaNow.AddHours(2), 60, initialQuotaNow.AddHours(2)).Single().UsedPercent == 25, "initial quota captures the first observed valid weekly reading and never replaces it later that day");
Assert(initialStore.CaptureInitial(initialQuotaNow.AddDays(1), 40, initialQuotaNow.AddDays(1)).Count == 2 && initialStore.ReadInitial().Single(x => x.At.LocalDateTime.Date == initialQuotaNow.AddDays(1).LocalDateTime.Date).UsedPercent == 40, "initial quota starts a new opening reading automatically on the next local day");
Assert(initialStore.CaptureInitial(initialQuotaNow.AddDays(-1), 80, initialQuotaNow).Count == 2, "stale official snapshots never create an initial quota reading for the current day");
var legacyStore = new QuotaSnapshotStore(quotaStorePath);
Assert(legacyStore.ReadInitial().Count == 0, "legacy last-only quota history is never relabeled as historical opening quota");
var persistentInitialStore = new QuotaSnapshotStore(quotaStorePath);
_ = persistentInitialStore.CaptureInitial(initialQuotaNow, 25, initialQuotaNow);
var restartedInitialStore = new QuotaSnapshotStore(quotaStorePath);
var restartedInitial = restartedInitialStore.CaptureInitial(initialQuotaNow.AddHours(3), 70, initialQuotaNow.AddHours(3)).Single();
Assert(restartedInitial == new TimedQuotaUsage(initialQuotaNow, 25), "initial quota survives restart and never replaces the first observed same-day value after a reset or lower remaining quota");
var independentQuotaPath = Path.Combine(Path.GetTempPath(), "codex-tracker-quota-store-independent-" + Guid.NewGuid() + ".json");
Assert(new QuotaSnapshotStore(independentQuotaPath).ReadInitial().Count == 0, "custom quota stores in the same temporary directory retain independent initial-history files");
File.Delete(quotaStorePath);
var initialQuotaStorePath = Path.ChangeExtension(quotaStorePath, "initial.json"); if (File.Exists(initialQuotaStorePath)) File.Delete(initialQuotaStorePath);
var independentInitialQuotaStorePath = Path.ChangeExtension(independentQuotaPath, "initial.json"); if (File.Exists(independentInitialQuotaStorePath)) File.Delete(independentInitialQuotaStorePath);
var quotaRolloutRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-quota-rollout-" + Guid.NewGuid());
Directory.CreateDirectory(quotaRolloutRoot);
var quotaRolloutPath = Path.Combine(quotaRolloutRoot, "quota.jsonl");
File.WriteAllText(quotaRolloutPath, "{\"timestamp\":\"2026-08-10T09:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":20,\"window_minutes\":10080}}}}\n{\"timestamp\":\"2026-08-10T21:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":77,\"window_minutes\":300},\"secondary\":{\"used_percent\":12,\"window_minutes\":10080}}}}\n");
var quotaRolloutService = new LocalUsageAnalyticsService(() => quotaSeriesNow);
var quotaRollout = quotaRolloutService.Read(5.5m, quotaRolloutRoot);
Assert(quotaRollout.QuotaTimeline is { Count: 1 } && quotaRollout.QuotaTimeline[0] == new TimedQuotaUsage(new DateTimeOffset(2026, 8, 10, 21, 0, 0, TimeSpan.Zero), 12), "analytics reads the latest weekly Codex quota from secondary when primary is the five-hour window, even when token_count info is null");
File.AppendAllText(quotaRolloutPath, "{\"timestamp\":\"2026-08-11T21:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\",\"primary\":{\"used_percent\":30,\"window_minutes\":10080}}}}\n");
Assert(quotaRolloutService.Read(5.5m, quotaRolloutRoot).QuotaTimeline is { Count: 2 } && quotaRolloutService.FilesAppendedLastRead == 1, "quota extraction remains incremental on appended rollouts");
Directory.Delete(quotaRolloutRoot, true);
var vmQuotaPath = Path.Combine(Path.GetTempPath(), "codex-tracker-vm-quota-" + Guid.NewGuid() + ".json");
var vmQuotaNow = new DateTimeOffset(2026, 8, 12, 16, 0, 0, TimeSpan.FromHours(-3));
var vmQuota = new MainViewModel(new QuotaSnapshotStore(vmQuotaPath), () => vmQuotaNow);
var live70 = new RateLimitSnapshot([new("codex:primary", "Weekly", 70, null, 10080)], null, null, null, vmQuotaNow.AddHours(-1));
var delayed30 = new RateLimitSnapshot([new("codex:primary", "Weekly", 30, null, 10080)], null, null, null, vmQuotaNow.AddHours(-2));
vmQuota.ApplyQuota(live70); vmQuota.Apply(delayed30, new UsageAnalytics(0, 0, 0, 0, 0, [], QuotaTimeline: [new TimedQuotaUsage(vmQuotaNow.AddDays(-1), 20)]));
Assert(vmQuota.DailyQuotaSeries.Single(x => x.Day.Day == 12).UsedPercent == 70 && vmQuota.DailyQuotaSeries.Single(x => x.Day.Day == 11).UsedPercent == 20, "daily quota retains historical load and never regresses today when delayed analytics apply");
File.Delete(vmQuotaPath);
Assert(mainWindowSource.Contains("_startupAnalytics.BeginConnection();", StringComparison.Ordinal) && mainWindowSource.Contains("_startupAnalytics.IsCurrent(connectionGeneration)", StringComparison.Ordinal), "new app-server clients invalidate previous callbacks before asynchronous discovery and recheck generation on the dispatcher");

var trayOnlyExtendedStyle = TrayOnlyWindowPolicy.ToTrayOnlyExtendedStyle(TrayOnlyWindowPolicy.AppWindowExtendedStyle | 0x00000008L);
Assert((trayOnlyExtendedStyle & TrayOnlyWindowPolicy.ToolWindowExtendedStyle) != 0 && (trayOnlyExtendedStyle & TrayOnlyWindowPolicy.AppWindowExtendedStyle) == 0 && (trayOnlyExtendedStyle & 0x00000008L) != 0, "tray-only policy adds WS_EX_TOOLWINDOW, removes WS_EX_APPWINDOW, and preserves unrelated extended styles");

var compactGauge = CompactGaugeLayoutPolicy.ForWindow(new WidgetSize(62, 52));
Assert(compactGauge == new CompactGaugeLayout(42, 38), "minimum compact layout keeps the circular background exactly 4 DIP smaller than the 42 DIP gauge");
Assert(CompactGaugeLayoutPolicy.ForWindow(new WidgetSize(100, 100 / (62d / 52d))) == new CompactGaugeLayout(100 / (62d / 52d) - 10, 100 / (62d / 52d) - 14), "resized compact layout derives both vector circles from the final window height without Viewbox scaling");
Assert(NearlyEqual(CompactGaugeLayoutPolicy.FontSizeForWindow(new(62, 52)), 15.18) && NearlyEqual(CompactGaugeLayoutPolicy.FontSizeForWindow(new(81, 0)), 15.18 * 81d / 62d) && NearlyEqual(CompactGaugeLayoutPolicy.FontSizeForWindow(new(100, 0)), 15.18 * 100d / 62d), "compact font uses the 15% larger 15.18 DIP base size and scales proportionally with the widget width");
var compactQuotaGaugeSize = new CompactQuotaGaugeSizeConverter();
Assert(NearlyEqual((double)compactQuotaGaugeSize.Convert([96d, 2], typeof(double), "", System.Globalization.CultureInfo.InvariantCulture), 42) && NearlyEqual((double)compactQuotaGaugeSize.Convert([62d, 1], typeof(double), "", System.Globalization.CultureInfo.InvariantCulture), 42) && NearlyEqual((double)compactQuotaGaugeSize.Convert([160d, 2], typeof(double), "", System.Globalization.CultureInfo.InvariantCulture), 74), "dual compact layout gives each visible gauge the original responsive per-circle diameter rather than squeezing both into the single-gauge width");
var dualCompactResize = ManualResizeGeometry.ResizeCompact(new ResizeBounds(10, 10, 96, 52), new ResizeVector(100, 0), ResizeHandle.Right, new ResizeWorkArea(0, 0, 400, 400), 96, 160, 2d, 4d);
var dualCompactTopResize = ManualResizeGeometry.ResizeCompact(new ResizeBounds(10, 100, 96, 52), new ResizeVector(0, -100), ResizeHandle.Top, new ResizeWorkArea(0, 0, 400, 400), 96, 160, 2d, 4d);
Assert(dualCompactResize.Width == 160 && dualCompactResize.Height == 84 && dualCompactTopResize.Width == 160 && dualCompactTopResize.Bottom == 152, "dual compact resizing uses its affine width-to-height geometry and preserves the opposite bottom edge when the top edge grows");
var compactQuotaXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "MainWindow.xaml"));
var compactQuotaWindowSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "MainWindow.xaml.cs"));
Assert(compactQuotaXaml.Contains("x:Name=\"CompactQuotaGauges\" Orientation=\"Horizontal\" HorizontalAlignment=\"Center\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("Text=\"5h\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("Text=\"7d\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("ConverterParameter=\"Percent\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("x:Name=\"DetailedFiveHourQuota\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("Text=\"{Binding FiveHourReset}\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("x:Name=\"CompactQuotaBox\"", StringComparison.Ordinal) && compactQuotaXaml.Contains("Tag=\"both\">5h + 7d", StringComparison.Ordinal), "compact and detailed quota layouts retain centered percentages, exact selector choices, responsive dual-circle sizing, and a detected-only five-hour detail row");
Assert(compactQuotaWindowSource.Contains("new FrameworkElement[] { CompactFiveHourGauge, CompactWeeklyGauge }", StringComparison.Ordinal) && compactQuotaWindowSource.Contains("CompactWidthForStoredSize", StringComparison.Ordinal) && compactQuotaWindowSource.Contains("StoredCompactWidth", StringComparison.Ordinal), "compact resizing and persistence operate on both visible gauge hit regions while retaining the saved single-circle scale");
Assert(BackdropCompositionPolicy.ForMode(WidgetVisualMode.Compact) == new BackdropComposition(BackdropNonClientRendering.Disabled, BackdropCornerPreference.DoNotRound) && BackdropCompositionPolicy.ForMode(WidgetVisualMode.Detailed) == new BackdropComposition(BackdropNonClientRendering.Enabled, BackdropCornerPreference.Round) && BackdropCompositionPolicy.ForMode(WidgetVisualMode.Settings) == new BackdropComposition(BackdropNonClientRendering.Enabled, BackdropCornerPreference.Round), "backdrop composition disables non-client shadow composition only in compact mode");

var lightAccent = AccentPalette.Create("#FFB000", false);
var darkAccent = AccentPalette.Create("#FFB000", true);
Assert(lightAccent.BaseHex == "#FFB000" && darkAccent.BaseHex == "#FFB000", "accent palette preserves the user's canonical seed color across themes");
Assert(AccentPalette.DarkSurfaceHex == "#2D2D2D" && AccentPalette.ContrastRatio(lightAccent.AccentHex, "#F7F7F4") >= 4.5 && AccentPalette.ContrastRatio(darkAccent.AccentHex, AccentPalette.DarkSurfaceHex) >= 4.5, "derived accent text remains readable on light and the exact #2D2D2D dark primary surface");
Assert(lightAccent.SoftHex != lightAccent.AccentHex && lightAccent.HoverHex != lightAccent.AccentHex && lightAccent.GlowHex != lightAccent.AccentHex, "a single accent seed derives distinct soft, hover and glow tonal roles");
Assert(AccentPalette.ContrastRatio(lightAccent.AgentMetadataHex, "#F7F7F4") >= 4.5 && AccentPalette.ContrastRatio(darkAccent.AgentMetadataHex, AccentPalette.DarkSurfaceHex) >= 4.5 && AccentPalette.Saturation(lightAccent.AgentMetadataHex) < AccentPalette.Saturation(lightAccent.AccentHex) && AccentPalette.Saturation(darkAccent.AgentMetadataHex) < AccentPalette.Saturation(darkAccent.AccentHex), "agent model and effort receive a less saturated but contrast-safe accent role in both themes");
Assert(themeManagerSource.Contains("Set(\"Porcelain\", dark ? AccentPalette.DarkSurfaceHex", StringComparison.Ordinal) && themeManagerSource.Contains("Set(\"DetailedSurface\", dark ? \"#FF2D2D2D\"", StringComparison.Ordinal) && themeManagerSource.Contains("Set(\"GlassSurface\", dark ? \"#FF2D2D2D\"", StringComparison.Ordinal) && themeManagerSource.Contains("Set(\"SettingsSurface\", dark ? \"#FF2D2D2D\"", StringComparison.Ordinal), "dark primary, detailed, glass and settings surfaces consistently use the requested #2D2D2D base");
Assert(AccentPalette.Normalize("invalid") == AccentPalette.DefaultBaseHex && AccentPalette.Normalize("0d8f6f") == "#0D8F6F", "invalid accent settings fall back safely while valid hex colors normalize canonically");

LocalizationManager.Apply("en-US");
Assert(LocalizationManager.CurrentLanguageCode == "en-US" && LocalizationManager.Text("Settings") == "Settings" && LocalizationManager.TranslateKnown("Trabalhando") == "Working" && LocalizationManager.TranslateKnown("Conversa Codex") == "Codex conversation", "en-US localization translates static and known runtime agent text");
var englishResetAt = new DateTimeOffset(2026, 8, 15, 14, 0, 0, TimeSpan.Zero);
var englishLocalReset = englishResetAt.ToLocalTime().ToString("MM/dd 'at' h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
Assert(TokenPresentation.Format(1_234_567, "en-US") == "1.23 M" && CurrencyPresentation.FormatCost(12.5m, 0m, "USD", "en-US") == "US$ 12.50" && ResetCountdown.Format(englishResetAt, new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero), "en-US") == $"resets in 1d 2h ({englishLocalReset})" && WeeklyForecastCalculator.FormatProjectedPercent(99.5, "en-US") == "99.5%", "en-US localization formats tokens, currency, reset countdown and forecast percentages with English conventions");
LocalizationManager.Apply("unsupported");
Assert(LocalizationManager.CurrentLanguageCode == "pt-BR" && LocalizationManager.Text("Settings") == "Configurações", "unsupported languages safely normalize to pt-BR");

var dualMonitorWorkAreas = new[] { new WidgetScreenRect(0, 0, 1920, 1080), new WidgetScreenRect(1920, 0, 3840, 1080) };
var secondarySavedBounds = new WidgetScreenRect(2877, 176, 2877 + 62, 176 + 52);
Assert(WidgetPlacementPolicy.Restore(secondarySavedBounds, dualMonitorWorkAreas) == secondarySavedBounds, "saved compact widget position on a secondary monitor remains unchanged instead of being clamped to the primary work area");
var leftMonitorWorkAreas = new[] { new WidgetScreenRect(-1920, 0, 0, 1080), new WidgetScreenRect(0, 0, 1920, 1080) };
var leftSavedBounds = new WidgetScreenRect(-1500, 176, -1500 + 62, 176 + 52);
Assert(WidgetPlacementPolicy.Restore(leftSavedBounds, leftMonitorWorkAreas) == leftSavedBounds, "saved widget position on a negative-coordinate monitor remains unchanged");
var partiallyVisibleBounds = new WidgetScreenRect(1890, 176, 1890 + 62, 176 + 52);
Assert(WidgetPlacementPolicy.Restore(partiallyVisibleBounds, dualMonitorWorkAreas) == partiallyVisibleBounds, "partially visible widget position is not unnecessarily moved");
var removedMonitorBounds = new WidgetScreenRect(5000, 176, 5000 + 62, 176 + 52);
Assert(WidgetPlacementPolicy.Restore(removedMonitorBounds, dualMonitorWorkAreas) == new WidgetScreenRect(3778, 176, 3840, 228), "offscreen widget falls back inside the nearest available work area");

var rankingNow = DateTimeOffset.Now;
var rankingCycleEnd = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
var rankingCycleStart = rankingCycleEnd.AddMinutes(-10080);
var rankingAnalytics = new UsageAnalytics(0, 143, 0, 0, 0,
[
    new ModelUsage("month-model", 99, 9.9m, true, new TokenUsageBreakdown(9, 20, 30, 40, .9m, 1.2m, 3.3m, 4.5m)),
    new ModelUsage("no-tariff-model", 44, 0, false, new TokenUsageBreakdown(4, 10, 12, 18)),
    new ModelUsage("unknown", 33, 0, false), new ModelUsage("unknown-model", 22, 0, false)
], UsdBrl: 5m, ModelTimeline:
[
    new(rankingNow.AddMinutes(-1), "day-model", 11, 1.25m, true, new TokenUsageBreakdown(1, 2, 3, 5, .05m, .2m, .3m, .7m)),
    new(rankingCycleStart.AddMinutes(1), "cycle-model", 50, 2.5m, true, new TokenUsageBreakdown(5, 10, 15, 20, .1m, .4m, .75m, 1.25m)),
    new(rankingCycleStart.AddMinutes(-1), "outside-cycle-model", 99, 9.9m, true)
]);
var rankingViewModel = new MainViewModel();
rankingViewModel.Apply(new RateLimitSnapshot([new("codex:primary", "Weekly limit", 16, rankingCycleEnd, 10080)], null, null, null, rankingNow), rankingAnalytics);
Assert(rankingViewModel.Ranking.First().Model == "month-model" && rankingViewModel.Ranking.First().SecondaryText == "R$ 49,50" && rankingViewModel.Ranking.Single(row => row.Model == "no-tariff-model").SecondaryText == "sem tarifa", "monthly ranking shows the priced model cost converted with analytics USD/BRL and preserves the localized no-tariff state for unpriced models in the same secondary line");
var monthlyTooltip = rankingViewModel.Ranking.Single(row => row.Model == "month-model").Tooltip;
Assert(monthlyTooltip.Contains("Leitura em cache: 9", StringComparison.Ordinal) && monthlyTooltip.Contains("R$ 4,50", StringComparison.Ordinal) && monthlyTooltip.Contains("Entrada: 20", StringComparison.Ordinal) && monthlyTooltip.Contains("R$ 6,00", StringComparison.Ordinal) && monthlyTooltip.Contains("Saída: 30", StringComparison.Ordinal) && monthlyTooltip.Contains("R$ 16,50", StringComparison.Ordinal) && monthlyTooltip.Contains("Raciocínio: 40", StringComparison.Ordinal) && monthlyTooltip.Contains("R$ 22,50", StringComparison.Ordinal) && monthlyTooltip.Contains("Total: 99", StringComparison.Ordinal) && monthlyTooltip.EndsWith("R$ 49,50", StringComparison.Ordinal) && !monthlyTooltip.Contains("cache write", StringComparison.OrdinalIgnoreCase), "monthly ranking tooltip shows localized mutually exclusive token categories, each estimated cost, and the final total without cache write");
var monthlyTooltipData = rankingViewModel.Ranking.Single(row => row.Model == "month-model").TooltipData;
Assert(monthlyTooltipData.Title == "month-model" && monthlyTooltipData.Categories.Count == 4 && monthlyTooltipData.Categories[0] == new TokenUsageTooltipLine("Leitura em cache", "9", "R$ 4,50", 9d / 99d) && monthlyTooltipData.Categories[3] == new TokenUsageTooltipLine("Raciocínio", "40", "R$ 22,50", 40d / 99d) && monthlyTooltipData.Total == new TokenUsageTooltipLine("Total", "99", "R$ 49,50", 1d) && monthlyTooltipData.EstimateNote == "Valores estimados", "ranking supplies complete localized tooltip data with token-share fractions without parsing its retained accessibility text");
var zeroTooltipData = TokenUsageTooltip.Create("zero-model", TokenUsageBreakdown.Zero, false, 1m, "USD");
Assert(zeroTooltipData.Categories.Count == 4 && zeroTooltipData.Categories.All(line => line.Fraction == 0d) && zeroTooltipData.Total.Fraction == 0d, "tooltip token-share fractions are zero when total tokens are zero");
Assert(rankingViewModel.Ranking.Single(row => row.Model == "no-tariff-model").Tooltip.Contains("sem tarifa", StringComparison.Ordinal), "unpriced model tooltip retains the localized no-tariff state instead of fabricating category costs");
Assert(rankingViewModel.Ranking.All(row => row.Model != "unknown") && rankingViewModel.Ranking.Single(row => row.Model == "Modelo não registrado").Tokens == "33" && rankingViewModel.Ranking.Single(row => row.Model == "unknown-model").Tokens == "22", "ranking localizes only the internal unknown bucket without hiding tokens or renaming literal model names");
rankingViewModel.SetCurrency("USD");
Assert(rankingViewModel.Ranking.First().SecondaryText == "US$ 9,90", "monthly ranking refreshes priced costs when the selected currency changes to USD");
LocalizationManager.Apply("en-US");
rankingViewModel.RefreshLocalization();
Assert(rankingViewModel.Ranking.First().SecondaryText == "US$ 9.90" && rankingViewModel.Ranking.Single(row => row.Model == "no-tariff-model").SecondaryText == "no tariff" && rankingViewModel.Ranking.Single(row => row.Model == "Model not recorded").Tokens == "33" && rankingViewModel.Ranking.Any(row => row.Model == "unknown-model"), "ranking refreshes cost formatting, unpriced text and the internal unknown label when the interface language changes to en-US without renaming literal model names");
rankingViewModel.SetCurrency("BRL");
Assert(rankingViewModel.Ranking.First().SecondaryText == "R$ 49.50", "ranking refreshes BRL cost formatting using the active en-US culture");
rankingViewModel.IsRankingDay = true;
Assert(rankingViewModel.Ranking.Single().Model == "day-model" && rankingViewModel.Ranking.Single().SecondaryText == "R$ 6.25", "day ranking filters model usage and uses the day CostUsd rather than the monthly aggregate");
var dayTooltip = rankingViewModel.Ranking.Single().Tooltip;
Assert(dayTooltip.Contains("Cache read: 1", StringComparison.Ordinal) && dayTooltip.Contains("R$ 0.25", StringComparison.Ordinal) && dayTooltip.Contains("Input: 2", StringComparison.Ordinal) && dayTooltip.Contains("R$ 1.00", StringComparison.Ordinal) && dayTooltip.Contains("Output: 3", StringComparison.Ordinal) && dayTooltip.Contains("R$ 1.50", StringComparison.Ordinal) && dayTooltip.Contains("Reasoning: 5", StringComparison.Ordinal) && dayTooltip.Contains("R$ 3.50", StringComparison.Ordinal) && dayTooltip.Contains("Total: 11", StringComparison.Ordinal) && dayTooltip.EndsWith("R$ 6.25", StringComparison.Ordinal) && !dayTooltip.Contains("cache write", StringComparison.OrdinalIgnoreCase), "day ranking tooltip uses its filtered breakdown and localized category costs");
rankingViewModel.IsRankingWeek = true;
Assert(rankingViewModel.Ranking.Any(row => row.Model == "cycle-model" && row.SecondaryText == "R$ 12.50") && rankingViewModel.Ranking.All(row => row.Model != "outside-cycle-model"), "week ranking uses only the active Codex quota cycle and its period CostUsd, excluding usage outside the cycle");
var weekTooltip = rankingViewModel.Ranking.Single(row => row.Model == "cycle-model").Tooltip;
Assert(weekTooltip.Contains("Cache read: 5", StringComparison.Ordinal) && weekTooltip.Contains("R$ 0.50", StringComparison.Ordinal) && weekTooltip.Contains("Input: 10", StringComparison.Ordinal) && weekTooltip.Contains("R$ 2.00", StringComparison.Ordinal) && weekTooltip.Contains("Output: 15", StringComparison.Ordinal) && weekTooltip.Contains("R$ 3.75", StringComparison.Ordinal) && weekTooltip.Contains("Reasoning: 20", StringComparison.Ordinal) && weekTooltip.Contains("R$ 6.25", StringComparison.Ordinal) && weekTooltip.Contains("Total: 50", StringComparison.Ordinal) && weekTooltip.EndsWith("R$ 12.50", StringComparison.Ordinal) && !weekTooltip.Contains("cache write", StringComparison.OrdinalIgnoreCase), "week ranking tooltip uses only the official-cycle model breakdown with localized category costs and total");
LocalizationManager.Apply("pt-BR");
rankingViewModel.RefreshLocalization();
rankingViewModel.ApplyQuota(new RateLimitSnapshot([], null, null, null, rankingNow));
Assert(rankingViewModel.Ranking.Count == 0, "week ranking is empty when the official active Codex quota cycle is unavailable");

var compactSize = WidgetSizePolicy.Normalize(WidgetVisualMode.Compact, new WidgetSize(999, 1));
Assert(compactSize.Width == 100 && Math.Abs(compactSize.Height * 62 - compactSize.Width * 52) < 0.001, "compact size clamps width at 100 and preserves the 62:52 ratio");
Assert(WidgetSizePolicy.Normalize(WidgetVisualMode.Detailed, new WidgetSize(1, 999)) == new WidgetSize(300, 720), "detailed size keeps fixed width and clamps visible height");
Assert(WidgetSizePolicy.Normalize(WidgetVisualMode.Detailed, new WidgetSize(300, 300)) == new WidgetSize(300, 300) && WidgetSizePolicy.Normalize(WidgetVisualMode.Detailed, new WidgetSize(300, 1)) == new WidgetSize(300, 260), "detailed preserves resized heights down to 260 and clamps below it");
Assert(WidgetSizePolicy.Normalize(WidgetVisualMode.Settings, new WidgetSize(double.NaN, 0)) == WidgetSizePolicy.Default(WidgetVisualMode.Settings), "invalid settings size falls back to its safe default");
Assert(WidgetSizePolicy.SettingsHeightForContent(612.2, 1000) == 613 && WidgetSizePolicy.SettingsHeightForContent(1000, 600) == 600 && WidgetSizePolicy.SettingsHeightForContent(100, 300) == 300, "settings content height expands to the required whole DIP while respecting the work-area and policy cap");
Assert(WidgetSizePolicy.DetailedMaxHeightForContent(512.2) == 513 && WidgetSizePolicy.DetailedMaxHeightForContent(999) == WidgetSizePolicy.DetailedMaxHeight, "detailed maximum height follows rounded content height without exceeding the safety cap");
Assert(WidgetSizePolicy.DetailedHeightForContent(512.2, 1000) == 513 && WidgetSizePolicy.DetailedHeightForContent(1000, 600) == 600 && WidgetSizePolicy.DetailedHeightForContent(100, 220) == 220, "detailed content height expands to the required whole DIP while respecting both the work-area and detailed safety cap");
var independentSlots = WidgetSizePolicy.NormalizeSlots(new WidgetModeSizes(new(124, 10), new(300, 480), new(300, 600)), false, new(62, 52));
Assert(WidgetSizePolicy.Get(independentSlots, WidgetVisualMode.Compact) == new WidgetSize(100, 100 / (62d / 52d)) && WidgetSizePolicy.Get(independentSlots, WidgetVisualMode.Detailed) == new WidgetSize(300, 480) && WidgetSizePolicy.Get(independentSlots, WidgetVisualMode.Settings) == new WidgetSize(300, 600), "persisted compact widths above 100 clamp without changing detailed and settings slots");
var conceptualTransition = WidgetSizePolicy.With(independentSlots, WidgetVisualMode.Compact, new(200, 1));
Assert(WidgetSizePolicy.Get(conceptualTransition, WidgetVisualMode.Compact) == new WidgetSize(100, 100 / (62d / 52d)) && WidgetSizePolicy.Get(independentSlots, WidgetVisualMode.Detailed) == new WidgetSize(300, 480) && WidgetSizePolicy.Get(independentSlots, WidgetVisualMode.Settings) == new WidgetSize(300, 600), "compact manual resize saves only a normalized compact slot");
var compactDetailedCompact = new WidgetModeSizes(new(100, 1), new(300, 533), new(300, 600));
var restoredCompact = WidgetSizePolicy.SelectModeSize(compactDetailedCompact, WidgetVisualMode.Compact);
var restoredDetailed = WidgetSizePolicy.SelectModeSize(compactDetailedCompact, WidgetVisualMode.Detailed);
var restoredCompactAgain = WidgetSizePolicy.SelectModeSize(compactDetailedCompact, WidgetVisualMode.Compact);
Assert(restoredCompact == new WidgetSize(100, 100 / (62d / 52d)) && restoredDetailed == new WidgetSize(300, 533) && restoredCompactAgain == restoredCompact, "compact cap survives repeated compact-detailed-compact selection without transient detailed dimensions");
var legacyCompact = SettingsStore.Normalize(new AppSettings(Width: 124, Height: 99, IsExpanded: false));
Assert(WidgetSizePolicy.Get(legacyCompact.ModeSizes!, WidgetVisualMode.Compact) == new WidgetSize(100, 100 / (62d / 52d)) && WidgetSizePolicy.Get(legacyCompact.ModeSizes!, WidgetVisualMode.Detailed) == WidgetSizePolicy.Default(WidgetVisualMode.Detailed), "legacy compact JSON migrates and clamps its dimensions only into compact");
var legacyDetailed = SettingsStore.Normalize(new AppSettings(Width: 222, Height: 480, IsExpanded: true));
Assert(WidgetSizePolicy.Get(legacyDetailed.ModeSizes!, WidgetVisualMode.Detailed) == new WidgetSize(300, 480) && WidgetSizePolicy.Get(legacyDetailed.ModeSizes!, WidgetVisualMode.Compact) == WidgetSizePolicy.Default(WidgetVisualMode.Compact), "legacy detailed JSON migrates its dimensions only into detailed");
var missingSizes = SettingsStore.Normalize(new AppSettings(ModeSizes: null));
Assert(missingSizes.ModeSizes is not null && WidgetSizePolicy.Get(missingSizes.ModeSizes, WidgetVisualMode.Settings) == WidgetSizePolicy.Default(WidgetVisualMode.Settings), "absent mode slots receive safe defaults");
var serializedSettings = legacyDetailed with { ModeSizes = new WidgetModeSizes(new(100, 1), new(300, 500), new(300, 650)) };
var roundTrippedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(SettingsStore.Normalize(serializedSettings)))!;
Assert(WidgetSizePolicy.Get(roundTrippedSettings.ModeSizes!, WidgetVisualMode.Compact) == new WidgetSize(100, 100 / (62d / 52d)) && WidgetSizePolicy.Get(roundTrippedSettings.ModeSizes!, WidgetVisualMode.Detailed) == new WidgetSize(300, 500) && WidgetSizePolicy.Get(roundTrippedSettings.ModeSizes!, WidgetVisualMode.Settings) == new WidgetSize(300, 650), "new JSON round trip retains all three size slots");

var settingsTestDirectory = Path.Combine(Path.GetTempPath(), "CodexTracker.Tests", Guid.NewGuid().ToString("N"));
var settingsTestPath = Path.Combine(settingsTestDirectory, "settings.json");
try
{
    var persistedUnread = new CompletedAgentWork("thread:turn", "thread", "Agent", "Entrega", "Concluído", "gpt-5.6-terra", "medium", DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow);
    var newerPersistedUnread = persistedUnread with { CompletionId = "thread:new-turn", CompletedAt = persistedUnread.CompletedAt.AddMinutes(1) };
    var persistedSettings = new AppSettings(Left: 412.5, Top: 237.25, IsExpanded: true, IsTopmost: false, CodexPath: @"C:\\Tools\\codex.exe", UsdBrl: 5.89m, Theme: "Escuro", CurrencyCode: "USD", ModeSizes: new WidgetModeSizes(new(90, 1), new(300, 480), new(300, 620)), IsAgentListExpanded: true, AccentColor: "#FFB000", LanguageCode: "en-US", UnreadAgentWorks: [persistedUnread, newerPersistedUnread]);
    new SettingsStore(settingsTestPath).Save(persistedSettings);
    var reloadedSettings = new SettingsStore(settingsTestPath).Load();
    Assert(reloadedSettings.Left == 412.5 && reloadedSettings.Top == 237.25 && reloadedSettings.IsExpanded && !reloadedSettings.IsTopmost && reloadedSettings.CodexPath == @"C:\\Tools\\codex.exe" && reloadedSettings.UsdBrl == 5.89m && reloadedSettings.Theme == "Escuro" && reloadedSettings.CurrencyCode == "USD" && reloadedSettings.IsAgentListExpanded && reloadedSettings.AccentColor == "#FFB000" && reloadedSettings.LanguageCode == "en-US" && reloadedSettings.UnreadAgentWorks?.Single().CompletionId == "thread:new-turn", "settings round trip keeps only the latest unread execution per root chat while preserving existing preferences");
}
finally
{
    if (Directory.Exists(settingsTestDirectory)) Directory.Delete(settingsTestDirectory, true);
}
Assert(!Directory.Exists(settingsTestDirectory), "temporary settings round-trip directory is removed after the test");
Assert(SettingsStore.Normalize(new AppSettings(AccentColor: "not-a-color")).AccentColor == AccentPalette.DefaultBaseHex, "invalid persisted accent colors migrate to the safe default");
Assert(SettingsStore.Normalize(new AppSettings(LanguageCode: "fr-FR")).LanguageCode == "pt-BR", "unsupported persisted languages migrate to pt-BR");
Assert(SettingsStore.Normalize(new AppSettings(CompactQuotaDisplay: "5h")).CompactQuotaDisplay == "5h" && SettingsStore.Normalize(new AppSettings(CompactQuotaDisplay: "7d")).CompactQuotaDisplay == "7d" && SettingsStore.Normalize(new AppSettings(CompactQuotaDisplay: "both")).CompactQuotaDisplay == "both" && SettingsStore.Normalize(new AppSettings(CompactQuotaDisplay: "unknown")).CompactQuotaDisplay == "both", "compact quota selection persists only five-hour, seven-day, or both with both as the safe default");
Assert(WidgetVisibilityPolicy.ShouldShow(true, false, false, true, false) && WidgetVisibilityPolicy.ShouldShow(false, true, false, true, false), "active and unread completed work force visibility even while Codex is backgrounded or minimized");
Assert(WidgetVisibilityPolicy.ShouldShow(false, false, true, false, false) && WidgetVisibilityPolicy.ShouldShow(false, false, false, false, true), "Codex foreground and direct widget interaction keep the widget visible");
Assert(!WidgetVisibilityPolicy.ShouldShow(false, false, false, false, false) && !WidgetVisibilityPolicy.ShouldShow(false, false, true, true, false), "idle widget hides immediately when Codex is backgrounded or minimized");
Assert(CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.810.4967.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe") && CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.810.4967.0_x64__2p2nqsd0c76g0\app\resources\codex.exe") && !CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\Users\user\.codex\plugins\.plugin-appserver\codex.exe") && !CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\Tools\ChatGPT.exe"), "desktop window detection accepts the real ChatGPT host and packaged Codex process while rejecting unrelated or CLI executables");
var codexDesktopPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.810.4967.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
Assert(!CodexDesktopWindowMonitor.Observe(codexDesktopPath, false, false, false).IsForeground && !CodexDesktopWindowMonitor.Observe(codexDesktopPath, true, true, false).IsForeground && CodexDesktopWindowMonitor.Observe(codexDesktopPath, true, false, false).IsForeground && CodexDesktopWindowMonitor.Observe(codexDesktopPath, true, false, true) is { IsForeground: true, IsMinimized: true }, "desktop window observation rejects hidden and cloaked Codex HWNDs while retaining a visible minimized Codex window for the visibility policy");
Assert(!mainWindowSource.Contains("CodexDesktopUnreadThreadIndex", StringComparison.Ordinal) && !mainWindowSource.Contains("CodexDesktopUnreadReconciliation", StringComparison.Ordinal), "Codex unread-index absence never removes Tracker completions automatically, even when another Codex chat has focus");

var agentRowsViewModel = new MainViewModel();
Assert(CodexThreadDeepLink.TryCreate("018f18cc-9ffc-7bb3-9a48-7a3df5372adc", out var validThreadLink) && validThreadLink!.AbsoluteUri == "codex://threads/018f18cc-9ffc-7bb3-9a48-7a3df5372adc", "thread deep links accept a canonical UUID and target exactly its Codex thread");
Assert(!CodexThreadDeepLink.TryCreate("codex://threads/018f18cc-9ffc-7bb3-9a48-7a3df5372adc", out _) && !CodexThreadDeepLink.TryCreate("not-a-uuid", out _) && !CodexThreadDeepLink.TryCreate(null, out _), "thread deep links reject non-UUID input before shell execution");
var mainWindowXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "MainWindow.xaml"));
var dailyUsageChartSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "DailyUsageChart.cs"));
var indicatorStart = mainWindowXaml.IndexOf("x:Name=\"AgentIndicatorButton\"", StringComparison.Ordinal);
var indicatorEnd = mainWindowXaml.IndexOf("<Popup x:Name=\"AgentListPopup\"", indicatorStart, StringComparison.Ordinal);
var indicatorTemplate = indicatorStart >= 0 && indicatorEnd > indicatorStart ? mainWindowXaml.Substring(indicatorStart, indicatorEnd - indicatorStart) : string.Empty;
Assert(indicatorTemplate.Contains("<Trigger Property=\"IsMouseOver\" Value=\"True\">", StringComparison.Ordinal) && indicatorTemplate.Contains("TargetName=\"AgentCount\" Property=\"Visibility\" Value=\"Collapsed\"", StringComparison.Ordinal) && indicatorTemplate.Contains("TargetName=\"AgentArrow\" Property=\"Visibility\" Value=\"Visible\"", StringComparison.Ordinal) && !indicatorTemplate.Contains("<MultiDataTrigger>", StringComparison.Ordinal), "agent indicator hover has one direct IsMouseOver trigger that swaps the count for the chevron independently of open state");
Assert(indicatorTemplate.Contains("x:Name=\"AgentIndicatorSurface\" Background=\"#2D2D2D\"", StringComparison.Ordinal) && indicatorTemplate.Contains("<Border.Effect><DropShadowEffect BlurRadius=\"5\" ShadowDepth=\"1\" Opacity=\".30\" Color=\"#151A18\" /></Border.Effect>", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"CompactQuotaGauges\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"CompactWeeklyGauge\"", StringComparison.Ordinal) && mainWindowXaml.Contains("<Ellipse.Effect><DropShadowEffect BlurRadius=\"5\" ShadowDepth=\"1\" Opacity=\".30\" Color=\"#151A18\" /></Ellipse.Effect>", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"AgentIndicatorButton\" Width=\"20\" Height=\"20\" Padding=\"0\" Margin=\"0,-1,0,5\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"WindowSurface\" CornerRadius=\"12\" ClipToBounds=\"True\"", StringComparison.Ordinal) && mainWindowSource.Contains("private const double CompactAgentIndicatorHeight = 24d;", StringComparison.Ordinal), "compact quota gauges and dark agent indicator use coherent subtle shadows with explicit lower space while the rounded window keeps its intended clipping");
Assert(indicatorTemplate.Contains("x:Name=\"AgentIndicatorSurface\" Background=\"#2D2D2D\" CornerRadius=\"10\" ClipToBounds=\"True\"", StringComparison.Ordinal) && indicatorTemplate.Contains("<Path x:Name=\"AgentWorkSpinner\" Width=\"18\" Height=\"18\" Data=\"M9,0 A9,9 0 0 1 14.29,1.72\" Stretch=\"None\" Stroke=\"#FFFFFF\" StrokeThickness=\"1\" Opacity=\".50\"", StringComparison.Ordinal) && indicatorTemplate.Contains("<RotateTransform x:Name=\"AgentWorkSpinnerRotation\" Angle=\"0\" />", StringComparison.Ordinal) && indicatorTemplate.Contains("<DataTrigger Binding=\"{Binding IsWorkAnimationEnabled}\" Value=\"True\">", StringComparison.Ordinal) && indicatorTemplate.Contains("x:Name=\"AgentWorkSpinnerStoryboard\"", StringComparison.Ordinal) && indicatorTemplate.Contains("Storyboard.TargetName=\"AgentWorkSpinnerRotation\" Storyboard.TargetProperty=\"Angle\" From=\"0\" To=\"-360\" Duration=\"0:0:1.00\"", StringComparison.Ordinal) && indicatorTemplate.Contains("<Storyboard RepeatBehavior=\"Forever\">", StringComparison.Ordinal) && !indicatorTemplate.Contains("AgentWorkGlow", StringComparison.Ordinal) && !indicatorTemplate.Contains("RadialGradientBrush", StringComparison.Ordinal) && !indicatorTemplate.Contains("GradientStop", StringComparison.Ordinal) && !indicatorTemplate.Contains("<BlurEffect", StringComparison.Ordinal) && !indicatorTemplate.Contains("ScaleTransform", StringComparison.Ordinal) && !indicatorTemplate.Contains("M10,1 A9,9", StringComparison.Ordinal) && indicatorTemplate.Contains("<StopStoryboard BeginStoryboardName=\"AgentWorkSpinnerStoryboard\" />", StringComparison.Ordinal), "active agents display a clipped 10-percent half-opacity white spinner arc on the indicator edge with fixed geometry and counterclockwise rotation only while work animation is allowed and stops cleanly for reduced motion");
Assert(indicatorTemplate.Contains("Visibility=\"{Binding HasAgentIndicator", StringComparison.Ordinal) && indicatorTemplate.Contains("x:Name=\"AgentCompletedCheck\"", StringComparison.Ordinal) && indicatorTemplate.Contains("Binding=\"{Binding ShowsCompletedIndicator}\"", StringComparison.Ordinal) && indicatorTemplate.Contains("Property=\"Background\" Value=\"#DDF3E6\"", StringComparison.Ordinal), "completed unread work replaces the active count with a green check on a light-green indicator");
var agentListStart = mainWindowXaml.IndexOf("<Popup x:Name=\"AgentListPopup\"", StringComparison.Ordinal);
var agentListEnd = mainWindowXaml.IndexOf("</Popup>", agentListStart, StringComparison.Ordinal);
var agentListTemplate = agentListStart >= 0 && agentListEnd > agentListStart ? mainWindowXaml.Substring(agentListStart, agentListEnd - agentListStart) : string.Empty;
var roundedClipBorderSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "RoundedClipBorder.cs"));
Assert(agentListTemplate.Contains("x:Name=\"AgentListWrapper\" Width=\"288\" MaxHeight=\"350\" Background=\"Transparent\"", StringComparison.Ordinal) && agentListTemplate.Contains("<Border.Effect><DropShadowEffect BlurRadius=\"12\" ShadowDepth=\"3\" Opacity=\".28\" Color=\"#151A18\" /></Border.Effect>", StringComparison.Ordinal) && agentListTemplate.Contains("<local:RoundedClipBorder x:Name=\"AgentListClipSurface\" Padding=\"0\" CornerRadius=\"12\" Background=\"{DynamicResource DetailedSurface}\"", StringComparison.Ordinal) && agentListTemplate.Contains("x:Name=\"AgentRow\" Margin=\"0\"", StringComparison.Ordinal) && agentListTemplate.Contains("<ContentPresenter Margin=\"0,8\" HorizontalAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\" />", StringComparison.Ordinal) && agentListTemplate.Contains("<Border Margin=\"{Binding Indent}\" Padding=\"15,6\">", StringComparison.Ordinal) && roundedClipBorderSource.Contains("Clip = new RectangleGeometry(new Rect(RenderSize), radius, radius);", StringComparison.Ordinal), "agent list keeps its shadow on an outer un-clipped wrapper while an inner dynamically sized rounded geometry clips every contiguous full-width row interaction");
Assert(agentListTemplate.Contains("Text=\"{Binding ModelAndEffort}\"", StringComparison.Ordinal) && agentListTemplate.Contains("Foreground=\"{DynamicResource AgentMetadataAccent}\"", StringComparison.Ordinal), "agent model and effort bind to the contrast-safe muted accent resource instead of fixed opacity or generic secondary ink");
Assert(agentListTemplate.Contains("Visibility=\"{Binding ShowsProjectSeparator, Converter={StaticResource BooleanToVisibility}}\"", StringComparison.Ordinal) && agentListTemplate.Contains("<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>", StringComparison.Ordinal) && agentListTemplate.Contains("Text=\"{Binding ProjectName}\" FontSize=\"9.2\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource SoftInk}\" Opacity=\".62\"", StringComparison.Ordinal) && agentListTemplate.Contains("<Border Grid.Column=\"1\" Height=\"1\" Background=\"{DynamicResource InputSurface}\" Opacity=\".65\" VerticalAlignment=\"Center\" Margin=\"8,0,0,0\" />", StringComparison.Ordinal), "agent projects render a subtle name-left divider with the project line extending to its right");
Assert(agentListTemplate.Contains("x:Name=\"MarkAllCompletedAgentsReadButton\"", StringComparison.Ordinal) && agentListTemplate.Contains("Panel.ZIndex=\"1\"", StringComparison.Ordinal) && agentListTemplate.Contains("HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\"", StringComparison.Ordinal) && agentListTemplate.Contains("Background=\"{DynamicResource DetailedSurface}\"", StringComparison.Ordinal) && agentListTemplate.Contains("Visibility=\"{Binding CanMarkAllCompletedAgentsRead", StringComparison.Ordinal) && agentListTemplate.Contains("ToolTip=\"{DynamicResource Loc.MarkAllCompletedAgentsRead}\"", StringComparison.Ordinal) && agentListTemplate.Contains("AutomationProperties.Name=\"{DynamicResource Loc.MarkAllCompletedAgentsRead}\"", StringComparison.Ordinal) && agentListTemplate.Contains("Data=\"M2,12L7,17L16,8\"", StringComparison.Ordinal) && agentListTemplate.Contains("Data=\"M8,12L13,17L22,8\"", StringComparison.Ordinal), "the mark-all completed-work action is an accessible solid double-check overlay in the list's upper-right corner without reserving layout space");
Assert(mainWindowSource.Contains("work.Provider == AgentProvider.Codex && activeThreadIds.Contains(work.ThreadId)", StringComparison.Ordinal), "Codex agent refresh removes a completed row only when that same root chat starts running again");
Assert(agentListTemplate.Contains("<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>", StringComparison.Ordinal) && agentListTemplate.Contains("x:Name=\"KindLabel\" Text=\"{Binding Type}\" MaxWidth=\"58\"", StringComparison.Ordinal) && agentListTemplate.Contains("Margin=\"0,0,6,0\"", StringComparison.Ordinal) && agentListTemplate.Contains("Grid.Column=\"1\" Text=\"{Binding ModelAndEffort}\"", StringComparison.Ordinal) && agentListTemplate.Contains("TextTrimming=\"CharacterEllipsis\" HorizontalAlignment=\"Left\"", StringComparison.Ordinal) && agentListTemplate.Contains("x:Name=\"StatusLabel\" Text=\"{Binding Status}\"", StringComparison.Ordinal) && agentListTemplate.Contains("<StackPanel x:Name=\"CompletedStatus\" Orientation=\"Horizontal\" Visibility=\"Collapsed\">", StringComparison.Ordinal) && agentListTemplate.Contains("x:Name=\"CompletedRowCheck\"", StringComparison.Ordinal) && agentListTemplate.Contains("<Setter TargetName=\"StatusLabel\" Property=\"Visibility\" Value=\"Collapsed\" />", StringComparison.Ordinal) && agentListTemplate.Contains("<Setter TargetName=\"CompletedStatus\" Property=\"Visibility\" Value=\"Visible\" />", StringComparison.Ordinal) && !agentListTemplate.Contains("<TranslateTransform Y=\"-9\" />", StringComparison.Ordinal) && !agentListTemplate.Contains("TextAlignment=\"Right\"", StringComparison.Ordinal), "completed-row check sits immediately beside the completed status while elapsed remains right-aligned without increasing the status-row height");
Assert(mainWindowXaml.Contains("Text=\"{Binding Tokens}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"10\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Text=\"{Binding SecondaryText}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"8\" Foreground=\"{DynamicResource SoftInk}\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("Text=\"{Binding Cost}\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("Text=\"{Binding TariffNote}\"", StringComparison.Ordinal), "ranking rows use exactly one small numeric secondary line below tokens for either the estimated cost or the localized no-tariff text");
var tooltipPresentationSource = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "TokenUsageTooltip.cs"));
var tokenTooltipAppXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "App.xaml"));
Assert(mainWindowXaml.Contains("ToolTip Content=\"{Binding TooltipData}\" Style=\"{StaticResource TokenUsageToolTip}\"", StringComparison.Ordinal) && dailyUsageChartSource.Contains("_tooltip.SetResourceReference(StyleProperty, \"TokenUsageToolTip\")", StringComparison.Ordinal) && dailyUsageChartSource.Contains("TokenUsageTooltip.Create(", StringComparison.Ordinal) && tooltipPresentationSource.Contains("Line(\"CachedRead\"", StringComparison.Ordinal) && tooltipPresentationSource.Contains("Line(\"Reasoning\"", StringComparison.Ordinal) && tooltipPresentationSource.Contains("Line(\"Total\"", StringComparison.Ordinal) && !tooltipPresentationSource.Contains("cache write", StringComparison.OrdinalIgnoreCase), "ranking and daily usage hovers share one structured source for the four supported token categories and total without presenting cache write");
var tokenTooltipCategoriesStart = tokenTooltipAppXaml.IndexOf("<ItemsControl ItemsSource=\"{Binding Categories}\">", StringComparison.Ordinal);
var tokenTooltipCategoriesEnd = tokenTooltipAppXaml.IndexOf("</ItemsControl>", tokenTooltipCategoriesStart, StringComparison.Ordinal);
var tokenTooltipCategories = tokenTooltipCategoriesStart >= 0 && tokenTooltipCategoriesEnd > tokenTooltipCategoriesStart ? tokenTooltipAppXaml.Substring(tokenTooltipCategoriesStart, tokenTooltipCategoriesEnd - tokenTooltipCategoriesStart) : string.Empty;
var tokenTooltipTotalStart = tokenTooltipAppXaml.IndexOf("<Grid Grid.Row=\"3\">", StringComparison.Ordinal);
var tokenTooltipTotalEnd = tokenTooltipAppXaml.IndexOf("</Grid>", tokenTooltipTotalStart, StringComparison.Ordinal);
var tokenTooltipTotal = tokenTooltipTotalStart >= 0 && tokenTooltipTotalEnd > tokenTooltipTotalStart ? tokenTooltipAppXaml.Substring(tokenTooltipTotalStart, tokenTooltipTotalEnd - tokenTooltipTotalStart) : string.Empty;
Assert(tokenTooltipAppXaml.Contains("x:Key=\"TokenUsageToolTip\"", StringComparison.Ordinal) && tokenTooltipAppXaml.Contains("DataType=\"{x:Type local:TokenUsageTooltip}\"", StringComparison.Ordinal) && tokenTooltipAppXaml.Contains("ItemsSource=\"{Binding Categories}\"", StringComparison.Ordinal) && tokenTooltipAppXaml.Contains("{DynamicResource DetailedSurface}", StringComparison.Ordinal), "shared token tooltip retains the localized structured template and dynamic theme surface");
Assert(tokenTooltipCategories.Contains("<Grid.RowDefinitions><RowDefinition Height=\"Auto\" /><RowDefinition Height=\"Auto\" /></Grid.RowDefinitions>", StringComparison.Ordinal) && tokenTooltipCategories.Split(new[] { "<ProgressBar " }, StringSplitOptions.None).Length == 2 && tokenTooltipCategories.Contains("Value=\"{Binding Fraction}\" Maximum=\"1\" Height=\"3\"", StringComparison.Ordinal) && tokenTooltipCategories.Contains("Margin=\"0,3,0,0\"", StringComparison.Ordinal) && tokenTooltipCategories.Contains("Background=\"{DynamicResource Sage}\" Foreground=\"{DynamicResource Accent}\"", StringComparison.Ordinal) && !tokenTooltipTotal.Contains("<ProgressBar ", StringComparison.Ordinal), "shared token tooltip gives each of the four category-share bars its full height plus spacing and renders no total bar");
Assert(tokenTooltipCategories.IndexOf("Text=\"{Binding Cost}\"", StringComparison.Ordinal) < tokenTooltipCategories.IndexOf("Text=\" · \"", StringComparison.Ordinal) && tokenTooltipCategories.IndexOf("Text=\" · \"", StringComparison.Ordinal) < tokenTooltipCategories.IndexOf("Text=\"{Binding Tokens}\"", StringComparison.Ordinal) && tokenTooltipCategories.Contains("Text=\"{Binding Cost}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"8.5\" Foreground=\"{DynamicResource SoftInk}\"", StringComparison.Ordinal) && tokenTooltipCategories.Contains("Text=\"{Binding Tokens}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"9.5\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource Ink}\"", StringComparison.Ordinal), "category tooltip values render cost dot tokens with the requested numeric typography and contrast");
Assert(tokenTooltipTotal.IndexOf("Text=\"{Binding Total.Cost}\"", StringComparison.Ordinal) < tokenTooltipTotal.IndexOf("Text=\" · \"", StringComparison.Ordinal) && tokenTooltipTotal.IndexOf("Text=\" · \"", StringComparison.Ordinal) < tokenTooltipTotal.IndexOf("Text=\"{Binding Total.Tokens}\"", StringComparison.Ordinal) && tokenTooltipTotal.Contains("Text=\"{Binding Total.Cost}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"8.5\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource SoftInk}\"", StringComparison.Ordinal) && tokenTooltipTotal.Contains("Text=\"{Binding Total.Tokens}\" FontFamily=\"./assets/fonts/#Source Sans 3\" FontSize=\"10\" FontWeight=\"Bold\" Foreground=\"{DynamicResource Ink}\"", StringComparison.Ordinal), "total tooltip values render cost dot tokens with matching soft cost and high-contrast token colors");
Assert(mainWindowXaml.Contains("ToolTipService.InitialShowDelay=\"0\" ToolTipService.BetweenShowDelay=\"0\"", StringComparison.Ordinal), "ranking tooltip owner opens immediately without changing the daily chart hover policy");
var chatDetailsXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "ChatDetailsWindow.xaml"));
var chatDetailsCode = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "ChatDetailsWindow.xaml.cs"));
Assert(mainWindowXaml.IndexOf("<local:DailyUsageChart", StringComparison.Ordinal) < mainWindowXaml.IndexOf("Click=\"OpenChatDetails\"", StringComparison.Ordinal) && mainWindowSource.Contains("private void OpenChatDetails", StringComparison.Ordinal) && mainWindowSource.Contains("window.Closed +=", StringComparison.Ordinal), "details by chat is immediately below daily usage and safely recreates a secondary window after close");
var openChatDetailsStart = mainWindowSource.IndexOf("private void OpenChatDetails", StringComparison.Ordinal);
var openChatDetailsEnd = mainWindowSource.IndexOf("private void OpenAgentThread", openChatDetailsStart, StringComparison.Ordinal);
var openChatDetailsCode = openChatDetailsStart >= 0 && openChatDetailsEnd > openChatDetailsStart ? mainWindowSource.Substring(openChatDetailsStart, openChatDetailsEnd - openChatDetailsStart) : string.Empty;
Assert(openChatDetailsCode.IndexOf("_viewModel.ResetChatDetailsView();", StringComparison.Ordinal) < openChatDetailsCode.IndexOf("new ChatDetailsWindow", StringComparison.Ordinal) && openChatDetailsCode.IndexOf("_viewModel.ResetChatDetailsView();", StringComparison.Ordinal) > openChatDetailsCode.IndexOf("return;", StringComparison.Ordinal), "opening a new chat-details window resets its shared view state only after the already-open activation branch returns");
Assert(chatDetailsXaml.Contains("Width=\"308\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("MinWidth=\"260\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("<local:RoundedClipBorder Background=\"{DynamicResource DetailedSurface}\" CornerRadius=\"12\">", StringComparison.Ordinal) && !chatDetailsXaml.Contains("BorderBrush=", StringComparison.Ordinal) && chatDetailsXaml.Contains("FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("Click=\"CloseWindow\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("AutomationProperties.Name=\"{DynamicResource Loc.CloseWindow}\"", StringComparison.Ordinal) && !chatDetailsXaml.Contains("cache write", StringComparison.OrdinalIgnoreCase), "chat details uses the compact borderless root surface, Source Sans 3 numeric styling, and an accessible close button without cache-write wording");
Assert(chatDetailsXaml.Contains("Text=\"{Binding ChatSearch, UpdateSourceTrigger=PropertyChanged}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("Text=\"{DynamicResource Loc.SearchChats}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("HasVisibleChatProjects", StringComparison.Ordinal) && chatDetailsXaml.Contains("Click=\"ToggleProject\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("ItemsSource=\"{Binding Chats}\" Visibility=\"{Binding IsExpanded, Converter={StaticResource BooleanToVisibility}}\"", StringComparison.Ordinal) && !chatDetailsXaml.Contains("Cost", StringComparison.Ordinal) && !chatDetailsXaml.Contains("EstimateNote", StringComparison.Ordinal) && chatDetailsXaml.Contains("Value=\"{Binding CachedReadFraction}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("Value=\"{Binding InputFraction}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("Value=\"{Binding OutputFraction}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("Value=\"{Binding ReasoningFraction}\"", StringComparison.Ordinal) && !chatDetailsXaml.Contains("TotalFraction", StringComparison.Ordinal) && chatDetailsXaml.Split(new[] { "<ProgressBar " }, StringSplitOptions.None).Length == 5 && LocalizationManager.HasTextKey("SearchChats"), "chat details render collapsed lazy project groups with an explicit collapsed visibility guard, localized search, exactly four category bars, and no estimated costs or total bar");
var clearSearchStart = chatDetailsXaml.IndexOf("Click=\"ClearChatSearch\"", StringComparison.Ordinal);
var clearSearchEnd = clearSearchStart >= 0 ? chatDetailsXaml.IndexOf("</Button></Grid>", clearSearchStart, StringComparison.Ordinal) : -1;
var clearSearchButton = clearSearchStart >= 0 && clearSearchEnd > clearSearchStart ? chatDetailsXaml.Substring(clearSearchStart, clearSearchEnd - clearSearchStart) : string.Empty;
Assert(chatDetailsXaml.Contains("Click=\"ClearChatSearch\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("AutomationProperties.Name=\"{DynamicResource Loc.ClearSearch}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Setter Property=\"Visibility\" Value=\"Visible\"/>", StringComparison.Ordinal) && chatDetailsXaml.Contains("Padding=\"9,6,28,6\"", StringComparison.Ordinal) && clearSearchButton.Contains("Background=\"Transparent\" BorderThickness=\"0\" Cursor=\"Hand\"", StringComparison.Ordinal) && clearSearchButton.Contains("<ControlTemplate TargetType=\"Button\"><Border Background=\"Transparent\" BorderThickness=\"0\" Padding=\"{TemplateBinding Padding}\"><ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/>", StringComparison.Ordinal) && !clearSearchButton.Contains("HoverSurface", StringComparison.Ordinal) && chatDetailsCode.Contains("private void ClearChatSearch", StringComparison.Ordinal) && LocalizationManager.HasTextKey("ClearSearch"), "chat search reserves a right affordance and keeps its localized clear action transparent, clickable, and free of hover surfaces");
Assert(chatDetailsXaml.Contains("<Ellipse Canvas.Left=\"3\" Canvas.Top=\"3\" Width=\"16\" Height=\"16\" Stroke=\"{DynamicResource SoftInk}\" StrokeThickness=\"1.7\"/>", StringComparison.Ordinal) && chatDetailsXaml.Contains("Data=\"M16.65,16.65L21,21\" Stroke=\"{DynamicResource SoftInk}\" StrokeThickness=\"1.7\" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\"", StringComparison.Ordinal) && clearSearchButton.Contains("<Path Data=\"M18,6L6,18M6,6L18,18\" Stroke=\"{DynamicResource SoftInk}\" StrokeThickness=\"1.7\" StrokeStartLineCap=\"Round\" StrokeEndLineCap=\"Round\"/>", StringComparison.Ordinal), "chat search uses the Lucide-style canvas magnifier and matching rounded SoftInk clear X icon");
Assert(chatDetailsXaml.Contains("<local:RoundedClipBorder Background=\"{DynamicResource InputSurface}\" CornerRadius=\"8\" Padding=\"8\" Margin=\"0,1\">", StringComparison.Ordinal) && chatDetailsXaml.Contains("Click=\"ToggleProject\" Background=\"Transparent\"", StringComparison.Ordinal) && !chatDetailsXaml.Contains("ProjectHeaderSurface\" Background=\"{DynamicResource InputSurface}\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Grid HorizontalAlignment=\"Stretch\">", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/></Grid.ColumnDefinitions>", StringComparison.Ordinal) && chatDetailsXaml.Contains("x:Name=\"ProjectHeaderName\" Text=\"{Binding Project}\" FontSize=\"9.2\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource SoftInk}\" Opacity=\".62\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("x:Name=\"ProjectHeaderDivider\" Grid.Column=\"1\" Height=\".5\" Background=\"{DynamicResource SoftInk}\" Opacity=\".62\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("x:Name=\"ProjectHeaderChevron\" Grid.Column=\"2\" Width=\"10\" Height=\"5\" Stretch=\"Fill\" Data=\"M0,0 L5,5 L10,0\"", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Setter Property=\"Data\" Value=\"M0,5 L5,0 L10,5\"/>", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Setter TargetName=\"ProjectHeaderName\" Property=\"Foreground\" Value=\"{DynamicResource Ink}\"/>", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Setter TargetName=\"ProjectHeaderDivider\" Property=\"Background\" Value=\"{DynamicResource Ink}\"/>", StringComparison.Ordinal) && chatDetailsXaml.Contains("<Setter TargetName=\"ProjectHeaderChevron\" Property=\"Stroke\" Value=\"{DynamicResource Ink}\"/>", StringComparison.Ordinal), "project headers stay transparent with matching base SoftInk opacity and hover all three header elements without adding a background, while chats retain independent cards and a refined divider/chevron");
Assert(chatDetailsXaml.Contains("MouseLeftButtonDown=\"HeaderMouseLeftButtonDown\"", StringComparison.Ordinal) && chatDetailsCode.Contains("if (current is System.Windows.Controls.Button) return;", StringComparison.Ordinal) && chatDetailsCode.Contains("VisualTreeHelper.GetParent(current)", StringComparison.Ordinal) && !chatDetailsCode.Contains("MouseLeftButtonDown +=", StringComparison.Ordinal), "the secondary-window drag handler is limited to its header and excludes the close button and its visual descendants");
Assert(mainWindowXaml.Contains("Text=\"{Binding Reset}\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Text=\"{Binding Forecast}\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Text=\"{Binding TodayCost}\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Text=\"{Binding Coverage}\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Text=\"{Binding AppVersion}\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"RateBox\" FontFamily=\"./assets/fonts/#Source Sans 3\"", StringComparison.Ordinal) && dailyUsageChartSource.Contains("new System.Windows.Media.FontFamily(\"./assets/fonts/#Source Sans 3\")", StringComparison.Ordinal), "all displayed numeric data uses the same Source Sans 3 family as the weekly percentage");
Assert(mainWindowXaml.Contains("x:Name=\"AccentColorButton\" Click=\"ChooseAccentColor\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"AccentColorSwatch\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"AccentColorValue\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Color=\"{DynamicResource AccentGlow}\"", StringComparison.Ordinal), "settings expose an accessible accent color picker and the agent ripple glow follows the derived palette");
Assert(mainWindowXaml.Contains("x:Name=\"LanguageBox\" SelectionChanged=\"PreviewLanguage\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Tag=\"pt-BR\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Tag=\"en-US\"", StringComparison.Ordinal) && mainWindowXaml.Contains("{DynamicResource Loc.Settings}", StringComparison.Ordinal), "settings expose pt-BR/en-US selection and static UI strings use dynamic localization resources");
var rankingPeriodStyleStart = mainWindowXaml.IndexOf("<Style TargetType=\"RadioButton\">", StringComparison.Ordinal);
var rankingPeriodStyleEnd = mainWindowXaml.IndexOf("</Style>", rankingPeriodStyleStart, StringComparison.Ordinal);
var rankingPeriodStyle = rankingPeriodStyleStart >= 0 && rankingPeriodStyleEnd > rankingPeriodStyleStart ? mainWindowXaml.Substring(rankingPeriodStyleStart, rankingPeriodStyleEnd - rankingPeriodStyleStart) : string.Empty;
Assert(rankingPeriodStyle.Contains("<Setter Property=\"Cursor\" Value=\"Hand\" />", StringComparison.Ordinal), "ranking day/week/month selector advertises an interactive hand cursor through its shared radio style");
var localizedKeys = System.Text.RegularExpressions.Regex.Matches(mainWindowXaml, @"\{DynamicResource Loc\.([A-Za-z0-9]+)\}").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value).Distinct().ToArray();
LocalizationManager.Apply("pt-BR");
Assert(localizedKeys.All(LocalizationManager.HasTextKey), "every XAML localization key exists in both language catalogs");
LocalizationManager.Apply("en-US");
Assert(localizedKeys.All(key => !string.IsNullOrWhiteSpace(LocalizationManager.Text(key))), "every XAML localization key resolves to non-empty en-US text");
LocalizationManager.Apply("pt-BR");
var appXaml = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "App.xaml"));
Assert(appXaml.Contains("<Style x:Key=\"SettingsLabelStyle\" TargetType=\"TextBlock\">", StringComparison.Ordinal) && appXaml.Contains("<Setter Property=\"FontSize\" Value=\"11\" />", StringComparison.Ordinal) && appXaml.Contains("<Setter Property=\"FontWeight\" Value=\"SemiBold\" />", StringComparison.Ordinal) && appXaml.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource SoftInk}\" />", StringComparison.Ordinal), "settings labels share a semantic typography and color style");
var comboBoxTemplateStart = appXaml.IndexOf("<Style TargetType=\"ComboBox\">", StringComparison.Ordinal);
var comboBoxTemplateEnd = appXaml.IndexOf("</Style>", comboBoxTemplateStart, StringComparison.Ordinal);
var comboBoxTemplate = comboBoxTemplateStart >= 0 && comboBoxTemplateEnd > comboBoxTemplateStart ? appXaml.Substring(comboBoxTemplateStart, comboBoxTemplateEnd - comboBoxTemplateStart) : string.Empty;
var checkBoxTemplateStart = appXaml.IndexOf("<Style TargetType=\"CheckBox\">", StringComparison.Ordinal);
var checkBoxTemplateEnd = appXaml.IndexOf("</Style>", checkBoxTemplateStart, StringComparison.Ordinal);
var checkBoxTemplate = checkBoxTemplateStart >= 0 && checkBoxTemplateEnd > checkBoxTemplateStart ? appXaml.Substring(checkBoxTemplateStart, checkBoxTemplateEnd - checkBoxTemplateStart) : string.Empty;
Assert(comboBoxTemplate.Contains("Padding=\"{TemplateBinding Padding}\" Focusable=\"False\"", StringComparison.Ordinal) && comboBoxTemplate.Contains("<Border x:Name=\"Surface\" Background=\"{TemplateBinding Background}\" CornerRadius=\"9\" Padding=\"{TemplateBinding Padding}\">", StringComparison.Ordinal) && appXaml.Split(new[] { "TextElement.Foreground=\"{TemplateBinding Foreground}\"" }, StringSplitOptions.None).Length >= 4 && appXaml.Contains("<Setter Property=\"Padding\" Value=\"14,6\" />", StringComparison.Ordinal) && appXaml.Contains("<Setter Property=\"Cursor\" Value=\"Hand\" />", StringComparison.Ordinal), "the ComboBox forwards its configured left padding into the inner toggle surface while selection and items retain foreground contrast and a hand cursor");
Assert(checkBoxTemplate.Contains("<Border x:Name=\"Mark\" Width=\"16\" Height=\"16\" CornerRadius=\"5\" Background=\"{DynamicResource InputSurface}\">", StringComparison.Ordinal) && !checkBoxTemplate.Contains("x:Name=\"Mark\" Width=\"16\" Height=\"16\" CornerRadius=\"5\" Background=\"{DynamicResource InputSurface}\" Margin=", StringComparison.Ordinal) && checkBoxTemplate.Contains("<ContentPresenter Margin=\"13,0,0,0\" VerticalAlignment=\"Center\" TextElement.Foreground=\"{TemplateBinding Foreground}\" />", StringComparison.Ordinal), "the standard checkbox keeps a stable 13-DIP gap after its marker without relying on BulletDecorator margin behavior");
Assert(appXaml.Contains("<Style x:Key=\"SettingsChoiceStyle\" TargetType=\"CheckBox\"", StringComparison.Ordinal) && appXaml.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource SoftInk}\" />", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"DetailedBox\" Content=\"{DynamicResource Loc.DetailedMode}\" Style=\"{StaticResource SettingsChoiceStyle}\" Foreground=\"{DynamicResource SoftInk}\"", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"TopmostBox\" Content=\"{DynamicResource Loc.AlwaysOnTop}\" Style=\"{StaticResource SettingsChoiceStyle}\" Foreground=\"{DynamicResource SoftInk}\"", StringComparison.Ordinal), "settings checkbox choices share the semantic secondary foreground explicitly without affecting ThemeSwitch");
Assert(mainWindowSource.Contains("if (SettingsPanel.Visibility == Visibility.Visible) return ResizeEdge.None;", StringComparison.Ordinal) && mainWindowSource.Contains("if (SettingsPanel.Visibility == Visibility.Visible) return;", StringComparison.Ordinal) && mainWindowSource.Contains("ScheduleSettingsHeightForContent();", StringComparison.Ordinal) && mainWindowSource.Contains("WidgetSizePolicy.SettingsHeightForContent(SettingsPanel.DesiredSize.Height, workArea.Height)", StringComparison.Ordinal), "settings disables manual resize and fits its required content height within the monitor work area");
Assert(mainWindowSource.Contains("WidgetSizePolicy.DetailedHeightForContent(", StringComparison.Ordinal) && mainWindowSource.Contains("if (Math.Abs(contentMaxHeight - _detailedContentMaxHeight) < .5d) return;", StringComparison.Ordinal) && mainWindowSource.Contains("Height = contentMaxHeight;", StringComparison.Ordinal) && mainWindowSource.Contains("Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Top + workArea.Height - Height));", StringComparison.Ordinal), "detailed content applies its full permitted height only when its required size changes, preserving later manual resize while moving upward only when needed to stay inside the current monitor work area");
var progressStyleStart = appXaml.IndexOf("<Style TargetType=\"ProgressBar\">", StringComparison.Ordinal);
var progressStyleEnd = appXaml.IndexOf("</Style>", progressStyleStart, StringComparison.Ordinal);
var progressStyle = progressStyleStart >= 0 && progressStyleEnd > progressStyleStart ? appXaml.Substring(progressStyleStart, progressStyleEnd - progressStyleStart) : string.Empty;
Assert(!progressStyle.Contains("WorkGlow", StringComparison.Ordinal) && !progressStyle.Contains("IsWorkAnimationEnabled", StringComparison.Ordinal) && mainWindowXaml.Contains("IsWorking=\"{Binding IsCodexWorkAnimationEnabled}\"", StringComparison.Ordinal), "ranking progress bars stay static while the weekly circular quota gauges retain their dedicated work glow");
var reasoningGlowStart = mainWindowXaml.IndexOf("x:Name=\"ReasoningGlow\"", StringComparison.Ordinal);
var reasoningGlowEnd = mainWindowXaml.IndexOf("</DataTemplate>", reasoningGlowStart, StringComparison.Ordinal);
var reasoningGlowTemplate = reasoningGlowStart >= 0 && reasoningGlowEnd > reasoningGlowStart ? mainWindowXaml.Substring(reasoningGlowStart, reasoningGlowEnd - reasoningGlowStart) : string.Empty;
Assert(reasoningGlowTemplate.Contains("<LinearGradientBrush MappingMode=\"Absolute\" StartPoint=\"0,0.5\" EndPoint=\"64,0.5\">", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("<LinearGradientBrush.Transform><TranslateTransform x:Name=\"ReasoningGlowTransform\" X=\"-64\" /></LinearGradientBrush.Transform>", StringComparison.Ordinal) && !reasoningGlowTemplate.Contains("LinearGradientBrush.RelativeTransform", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("KeyTime=\"0:0:0\" Value=\"-64\"", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("KeyTime=\"0:0:2.00\" Value=\"268\"", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("KeyTime=\"0:0:4.00\" Value=\"268\"", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("<DataTrigger Binding=\"{Binding IsWorkAnimationEnabled}\" Value=\"True\">", StringComparison.Ordinal) && reasoningGlowTemplate.Contains("<StopStoryboard BeginStoryboardName=\"ReasoningGlowStoryboard\" />", StringComparison.Ordinal), "reasoning glow uses a fixed 64-DIP absolute band, a two-second left-to-right sweep, two-second hold, and the work-animation/reduced-motion trigger");
var mainWindowCode = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "src", "CodexTracker", "MainWindow.xaml.cs"));
Assert(!mainWindowXaml.Contains("Click=\"ToggleTopmost\"", StringComparison.Ordinal) && !mainWindowCode.Contains("private void ToggleTopmost", StringComparison.Ordinal) && mainWindowXaml.Contains("x:Name=\"TopmostBox\" Content=\"{DynamicResource Loc.AlwaysOnTop}\"", StringComparison.Ordinal) && mainWindowCode.Contains("Topmost = TopmostBox.IsChecked == true;", StringComparison.Ordinal), "the visible pin control and its dead handler are removed while the Settings topmost preference remains functional");
Assert(mainWindowXaml.Contains("x:Name=\"CodexPathFallbackPanel\" Visibility=\"Collapsed\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("Click=\"AutoDetect\"", StringComparison.Ordinal) && !mainWindowXaml.Contains("Click=\"TestPath\"", StringComparison.Ordinal), "manual Codex path UI is a collapsed fallback and does not expose the obsolete auto-detect or test actions");
Assert(mainWindowCode.Contains("var automaticallyDetectedPath = CodexExecutableDiscovery.Find(null);", StringComparison.Ordinal) && mainWindowCode.Contains("CodexPathFallbackPanel.Visibility = automaticallyDetectedPath is null ? Visibility.Visible : Visibility.Collapsed;", StringComparison.Ordinal) && mainWindowCode.Contains("PathBox.Text = automaticallyDetectedPath ?? _settings.CodexPath ?? \"\";", StringComparison.Ordinal), "settings show the manual Codex path fallback only when automatic discovery fails and otherwise keep the detected or persisted path available");
Assert(mainWindowCode.Contains("var manualCodexPath = CodexPathFallbackPanel.Visibility == Visibility.Visible", StringComparison.Ordinal) && mainWindowCode.Contains("? string.IsNullOrWhiteSpace(PathBox.Text) ? null : PathBox.Text", StringComparison.Ordinal) && mainWindowCode.Contains(": _settings.CodexPath;", StringComparison.Ordinal), "applying settings clears or accepts PathBox only while the fallback is visible and preserves the stored Codex path while it is hidden");
Assert(mainWindowCode.Contains("CreateTrayIcon(_settings.AccentColor)", StringComparison.Ordinal) && mainWindowCode.Contains("ColorTranslator.FromHtml(AccentPalette.Normalize(accentColor))", StringComparison.Ordinal) && mainWindowCode.Contains("CreateTray();", StringComparison.Ordinal), "tray fallback and recreated localized menu follow the persisted accent instead of retaining a fixed green");
Assert(mainWindowCode.Contains("previousIcon?.Dispose();", StringComparison.Ordinal) && mainWindowCode.Contains("previousMenu?.Dispose();", StringComparison.Ordinal) && mainWindowCode.Contains("_trayMenu?.Dispose();", StringComparison.Ordinal) && mainWindowCode.Contains("_trayIcon?.Dispose();", StringComparison.Ordinal), "repeated language previews replace and deterministically dispose tray icon and menu native resources");
Assert(mainWindowCode.Contains("LocalizationManager.Apply(_settings.LanguageCode);", StringComparison.Ordinal) && mainWindowCode.Contains("LanguageCode = language", StringComparison.Ordinal) && mainWindowCode.Contains("private void PreviewLanguage", StringComparison.Ordinal), "language preview, cancel restoration and apply persistence are wired through the settings lifecycle");
var refreshAgentsStart = mainWindowCode.IndexOf("private async Task RefreshAgentsAsync", StringComparison.Ordinal);
var refreshAgentsEnd = mainWindowCode.IndexOf("private void ToggleAgentList", refreshAgentsStart, StringComparison.Ordinal);
var refreshAgentsCode = refreshAgentsStart >= 0 && refreshAgentsEnd > refreshAgentsStart ? mainWindowCode.Substring(refreshAgentsStart, refreshAgentsEnd - refreshAgentsStart) : string.Empty;
Assert(refreshAgentsCode.Contains("else if (_settings.IsAgentListExpanded && !_viewModel.Expanded) _viewModel.IsAgentListOpen = true;", StringComparison.Ordinal), "agent refresh never opens the list while detailed mode is active");
Assert(mainWindowCode.Contains("_unreadAgentWorks[index] = work with { Title = title.Trim() };", StringComparison.Ordinal) && mainWindowCode.Contains("if (unreadChanged)", StringComparison.Ordinal) && mainWindowCode.Contains("PersistUnreadAgentWorks();", StringComparison.Ordinal), "late app-server titles replace and persist fallback titles for unread completed work");
Assert(mainWindowCode.Contains("_viewModel.ApplyAgentTitles(titles);", StringComparison.Ordinal), "late app-server titles immediately update active agent rows instead of waiting for the next activity snapshot");
Assert(mainWindowCode.Contains("work.Provider == AgentProvider.Codex && activeThreadIds.Contains(work.ThreadId)", StringComparison.Ordinal), "agent refresh permanently discards an old unread Codex completion when the same root chat starts running again");
var openAgentThreadStart = mainWindowCode.IndexOf("private void OpenAgentThread", StringComparison.Ordinal);
var openAgentThreadEnd = mainWindowCode.IndexOf("private void MarkThreadRead", openAgentThreadStart, StringComparison.Ordinal);
var openAgentThreadCode = openAgentThreadStart >= 0 && openAgentThreadEnd > openAgentThreadStart ? mainWindowCode.Substring(openAgentThreadStart, openAgentThreadEnd - openAgentThreadStart) : string.Empty;
Assert(openAgentThreadCode.Contains("Process.Start", StringComparison.Ordinal) && openAgentThreadCode.Contains("DataContext: AgentActivityRow { IsCompleted: true }", StringComparison.Ordinal) && openAgentThreadCode.Contains("MarkThreadRead(threadId);", StringComparison.Ordinal), "opening a completed Tracker row is the explicit action that marks only that thread as read");
var markAllReadStart = mainWindowCode.IndexOf("private void MarkAllCompletedAgentsRead", StringComparison.Ordinal);
var markAllReadEnd = mainWindowCode.IndexOf("private void PersistUnreadAgentWorks", markAllReadStart, StringComparison.Ordinal);
var markAllReadCode = markAllReadStart >= 0 && markAllReadEnd > markAllReadStart ? mainWindowCode.Substring(markAllReadStart, markAllReadEnd - markAllReadStart) : string.Empty;
Assert(markAllReadCode.Contains("_unreadAgentWorks.Clear();", StringComparison.Ordinal) && markAllReadCode.Split(new[] { "PersistUnreadAgentWorks();" }, StringSplitOptions.None).Length == 2 && markAllReadCode.Contains("_viewModel.MarkAllCompletedAgentsRead();", StringComparison.Ordinal) && !markAllReadCode.Contains("Process.Start", StringComparison.Ordinal), "mark-all clears unread completion persistence in one operation without opening chats");
var toggleDetailedStart = mainWindowCode.IndexOf("private void ToggleDetailed", StringComparison.Ordinal);
var toggleDetailedEnd = mainWindowCode.IndexOf("private void Settings", toggleDetailedStart, StringComparison.Ordinal);
var toggleDetailedCode = toggleDetailedStart >= 0 && toggleDetailedEnd > toggleDetailedStart ? mainWindowCode.Substring(toggleDetailedStart, toggleDetailedEnd - toggleDetailedStart) : string.Empty;
Assert(toggleDetailedCode.Contains("if (_viewModel.Expanded) _viewModel.IsAgentListOpen = false;", StringComparison.Ordinal) && toggleDetailedCode.Contains("else if (_settings.IsAgentListExpanded && _viewModel.HasAgentIndicator)", StringComparison.Ordinal) && toggleDetailedCode.Contains("_viewModel.IsAgentListOpen = true;", StringComparison.Ordinal) && toggleDetailedCode.Contains("RepositionAgentListPopup();", StringComparison.Ordinal) && !toggleDetailedCode.Contains("IsAgentListExpanded =", StringComparison.Ordinal), "detailed mode closes only the physical agent popup and compact mode restores it for active or unread-completed agents");
var agentRowsNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
var rootAgent = new ActiveAgent("root", null, 0, false, "Agent", "Principal", "Lendo", "gpt-5.6-sol", "high", agentRowsNow.AddMinutes(-1), agentRowsNow);
var childAgent = new ActiveAgent("child", "root", 1, true, "Subagent", "Filho", "Implementando", "gpt-5.6-terra", "medium", agentRowsNow.AddSeconds(-30), agentRowsNow);
agentRowsViewModel.ApplyAgents([rootAgent], agentRowsNow, false);
agentRowsViewModel.ApplyAgents([rootAgent, childAgent], agentRowsNow.AddSeconds(1), true, animationsEnabled: true);
Assert(agentRowsViewModel.ActiveAgents.Count == 2 && !agentRowsViewModel.ActiveAgents.Single(row => row.ThreadId == "root").IsNew && agentRowsViewModel.ActiveAgents.Single(row => row.ThreadId == "child").IsNew, "agent refresh preserves existing rows and flags only a newly appeared agent for entry animation");
var stableVisualRow = agentRowsViewModel.AgentItems[0];
agentRowsViewModel.ApplyAgents([rootAgent, childAgent], agentRowsNow.AddSeconds(2), false, animationsEnabled: true);
Assert(ReferenceEquals(stableVisualRow, agentRowsViewModel.AgentItems[0]), "unchanged active rows retain their visual identity so the reasoning glow completes its sweep and delay without restarting on each refresh");
agentRowsViewModel.ApplyAgentTitles(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["root"] = "Titulo definitivo" });
Assert(ReferenceEquals(stableVisualRow, agentRowsViewModel.AgentItems[0]) && stableVisualRow.Title == "Titulo definitivo" && agentRowsViewModel.AgentItems.Count == 2, "a definitive app-server title updates an active root row immediately without recreating it or affecting its child row");
agentRowsViewModel.MarkNewAgentRowsStable();
Assert(agentRowsViewModel.ActiveAgents.All(row => !row.IsNew), "agent row entry flags clear after their one-time animation");
var reducedMotionAgents = new MainViewModel();
reducedMotionAgents.ApplyAgents([rootAgent], agentRowsNow, false, animationsEnabled: false);
reducedMotionAgents.ApplyAgents([rootAgent, childAgent], agentRowsNow.AddSeconds(1), true, animationsEnabled: false);
Assert(reducedMotionAgents.ActiveAgents.All(row => !row.IsNew), "reduced-motion preference suppresses new-row entry animation deterministically");
var unreadWork = new CompletedAgentWork("root:turn", "root", "Agent", "Principal", "Concluído", "gpt-5.6-sol", "high", agentRowsNow.AddMinutes(-2), agentRowsNow);
agentRowsViewModel.ApplyUnreadCompletedAgents([unreadWork]);
Assert(agentRowsViewModel.HasActiveAgents && !agentRowsViewModel.HasUnreadCompletedAgents && agentRowsViewModel.AgentItems.Count == 2 && agentRowsViewModel.AgentItems.All(row => !row.IsCompleted), "an unread completion for a currently active root chat is discarded instead of duplicating its row");
var activeRowBeforeCompletedRefresh = agentRowsViewModel.AgentItems[0];
agentRowsViewModel.ApplyUnreadCompletedAgents([unreadWork, unreadWork, new CompletedAgentWork("sub:turn", "sub", "Subagent", "Filho", "Concluído", "gpt-5.6-terra", "medium", agentRowsNow.AddMinutes(-1), agentRowsNow)]);
Assert(agentRowsViewModel.AgentItems.Count == 2 && ReferenceEquals(activeRowBeforeCompletedRefresh, agentRowsViewModel.AgentItems[0]) && agentRowsViewModel.AgentItems.All(row => !row.IsCompleted), "refreshing stale unread completions retains active visual identities and excludes active-root, duplicate, and subagent completions");
agentRowsViewModel.ApplyAgents([], agentRowsNow, false);
agentRowsViewModel.ApplyUnreadCompletedAgents([unreadWork, unreadWork with { CompletionId = "root:older-turn", CompletedAt = agentRowsNow.AddMinutes(-1) }]);
var completedRowBeforeRestart = agentRowsViewModel.AgentItems.Single();
Assert(agentRowsViewModel.ShowsCompletedIndicator && completedRowBeforeRestart.IsCompleted && completedRowBeforeRestart.CompletionId == "root:turn", "completed unread work keeps only the latest execution per root chat");
var restartedRoot = rootAgent with { Status = "Trabalhando", StartedAt = agentRowsNow.AddSeconds(5), LastActivityAt = agentRowsNow.AddSeconds(10) };
agentRowsViewModel.ApplyAgents([restartedRoot], agentRowsNow.AddSeconds(10), true, animationsEnabled: true);
agentRowsViewModel.ApplyUnreadCompletedAgents([unreadWork]);
var restartedRow = agentRowsViewModel.AgentItems.Single();
Assert(ReferenceEquals(completedRowBeforeRestart, restartedRow) && !restartedRow.IsCompleted && restartedRow.Status == LocalizationManager.TranslateKnown("Trabalhando") && restartedRow.Elapsed == "0m 05s" && !agentRowsViewModel.HasUnreadCompletedAgents, "restarting the same root chat reuses its row, updates status, and resets elapsed time without duplication");
var markAllViewModel = new MainViewModel();
var independentUnreadWork = unreadWork with { ThreadId = "completed-root", CompletionId = "completed-root:turn" };
markAllViewModel.ApplyAgents([rootAgent], agentRowsNow, false);
markAllViewModel.ApplyUnreadCompletedAgents([independentUnreadWork, independentUnreadWork with { CompletionId = "completed-root:older", CompletedAt = agentRowsNow.AddMinutes(-1) }, unreadWork with { ThreadId = "subagent-root", Type = "Subagent" }]);
Assert(markAllViewModel.CanMarkAllCompletedAgentsRead && markAllViewModel.MarkAllCompletedAgentsRead() && !markAllViewModel.HasUnreadCompletedAgents && !markAllViewModel.CanMarkAllCompletedAgentsRead && markAllViewModel.AgentItems.Select(row => row.ThreadId).SequenceEqual(["root"]) && !markAllViewModel.MarkAllCompletedAgentsRead(), "mark-all removes only unread completed principal rows, immediately hides its state, and preserves active agents");

var projectGroupsViewModel = new MainViewModel();
var sameNameProjectA = rootAgent with { ThreadId = "project-a", Title = "Projeto A", ProjectPath = @"D:\Dev\same" };
var sameNameProjectB = rootAgent with { ThreadId = "project-b", Title = "Projeto B", ProjectPath = @"D:\Other\same" };
var noProjectAgent = rootAgent with { ThreadId = "without-project", Title = "Sem raiz", ProjectPath = null };
projectGroupsViewModel.ApplyAgents([sameNameProjectB, noProjectAgent, sameNameProjectA], agentRowsNow, false);
projectGroupsViewModel.ApplyUnreadCompletedAgents([new CompletedAgentWork("project-a:done", "project-a-completed", "Agent", "Concluído A", "Concluído", "gpt-5.6-sol", "high", agentRowsNow.AddMinutes(-2), agentRowsNow, @"D:\Dev\same")]);
Assert(projectGroupsViewModel.AgentItems.Select(row => row.ThreadId).SequenceEqual(["project-a", "project-a-completed", "project-b", "without-project"]) && projectGroupsViewModel.AgentItems.Select(row => row.ShowsProjectSeparator).SequenceEqual([true, false, true, true]) && projectGroupsViewModel.AgentItems.Last().ProjectName == "Sem projeto" && projectGroupsViewModel.AgentItems.Count(row => row.ProjectName == "same") == 3, "agent rows group by full project path deterministically, retain active work before completed work in each project, keep same-basename paths separate, and label missing paths Sem projeto");

var resizeWorkArea = new ResizeWorkArea(-1920, 0, 1920, 1080);
var compactStart = new ResizeBounds(-1500, 200, 124, 104);
var compactLeft = ManualResizeGeometry.ResizeCompact(compactStart, new(-100, 0), ResizeHandle.Left, resizeWorkArea);
Assert(compactLeft.Right == compactStart.Right && compactLeft.Top == compactStart.Top, "left compact resize preserves the opposite right/top anchor on a negative-coordinate monitor");
var compactRight = ManualResizeGeometry.ResizeCompact(compactStart, new(100, 0), ResizeHandle.Right, resizeWorkArea);
Assert(compactRight.Left == compactStart.Left && compactRight.Top == compactStart.Top, "right compact resize preserves the opposite left/top anchor");
var compactTop = ManualResizeGeometry.ResizeCompact(compactStart, new(0, -50), ResizeHandle.Top, resizeWorkArea);
Assert(compactTop.Bottom == compactStart.Bottom && compactTop.Left == compactStart.Left, "top compact resize preserves the opposite bottom/left anchor");
var compactBottom = ManualResizeGeometry.ResizeCompact(compactStart, new(0, 50), ResizeHandle.Bottom, resizeWorkArea);
Assert(compactBottom.Top == compactStart.Top && compactBottom.Left == compactStart.Left, "bottom compact resize preserves the opposite top/left anchor");
foreach (var handle in new[] { ResizeHandle.Left | ResizeHandle.Top, ResizeHandle.Right | ResizeHandle.Top, ResizeHandle.Left | ResizeHandle.Bottom, ResizeHandle.Right | ResizeHandle.Bottom })
{
    var resized = ManualResizeGeometry.ResizeCompact(compactStart, new(handle.HasFlag(ResizeHandle.Left) ? -80 : 80, handle.HasFlag(ResizeHandle.Top) ? -40 : 40), handle, resizeWorkArea);
    Assert((!handle.HasFlag(ResizeHandle.Left) || resized.Right == compactStart.Right) && (!handle.HasFlag(ResizeHandle.Right) || resized.Left == compactStart.Left) && (!handle.HasFlag(ResizeHandle.Top) || resized.Bottom == compactStart.Bottom) && (!handle.HasFlag(ResizeHandle.Bottom) || resized.Top == compactStart.Top), "compact corner resize preserves each opposite corner anchor");
}
var limitedCompact = ManualResizeGeometry.ResizeCompact(new(-1800, 100, 124, 104), new(-1000, 0), ResizeHandle.Left, resizeWorkArea);
Assert(limitedCompact.Left == -1920 && limitedCompact.Right == -1676 && limitedCompact.Right == -1676, "compact work-area limit stops at the monitor edge without moving the opposite anchor");
var maxCompact = ManualResizeGeometry.ResizeCompact(compactStart, new(1000, 0), ResizeHandle.Right, resizeWorkArea);
Assert(maxCompact.Width == 320 && maxCompact.Left == compactStart.Left && maxCompact.Height * 62 == maxCompact.Width * 52, "compact maximum and 62:52 ratio preserve the opposite anchor");
var minCompact = ManualResizeGeometry.ResizeCompact(compactStart, new(-1000, 0), ResizeHandle.Right, resizeWorkArea);
Assert(minCompact.Width == 62 && minCompact.Left == compactStart.Left, "compact minimum width preserves the opposite anchor");
var detailedStart = new ResizeBounds(-1400, 300, 300, 240);
var detailedTop = ManualResizeGeometry.ResizeVertical(detailedStart, new(500, -100), ResizeHandle.Top, resizeWorkArea, 50, 620);
Assert(detailedTop.Left == detailedStart.Left && detailedTop.Width == detailedStart.Width && detailedTop.Bottom == detailedStart.Bottom, "detailed/settings resize is vertical-only and preserves the bottom anchor from top handle");
var detailedBottom = ManualResizeGeometry.ResizeVertical(detailedStart, new(-500, 1000), ResizeHandle.Bottom, resizeWorkArea, 50, 620);
Assert(detailedBottom.Left == detailedStart.Left && detailedBottom.Width == detailedStart.Width && detailedBottom.Top == detailedStart.Top && detailedBottom.Height == 620, "detailed/settings bottom resize keeps horizontal bounds and honors maximum height");
var detailedMinimum = ManualResizeGeometry.ResizeVertical(detailedStart, new(0, 1000), ResizeHandle.Top, resizeWorkArea, 50, 620);
Assert(detailedMinimum.Height == 50 && detailedMinimum.Bottom == detailedStart.Bottom, "detailed/settings minimum height preserves the opposite anchor");

var payload = JsonDocument.Parse("""
{
  "rateLimits": {
    "planType": "pro",
    "credits": { "balance": "18.5", "hasCredits": true, "unlimited": false },
    "primary": { "usedPercent": 42.5, "resetsAt": 1786550400, "windowDurationMins": 300 },
    "secondary": null, "individualLimit": null
  },
  "rateLimitResetCredits": { "availableCount": 2 },
  "rateLimitsByLimitId": {
    "codex": { "limitName": "Codex historical", "primary": { "usedPercent": 42.5, "resetsAt": 1786550400, "windowDurationMins": 300 } },
    "codex_bengalfox": { "limitName": "GPT-5.3 Codex Spark", "primary": { "usedPercent": 67, "resetsAt": 1787000000, "windowDurationMins": 10080 } }
  }
}
""").RootElement;
var snapshot = RateLimitParser.Parse(payload, DateTimeOffset.UtcNow);
Assert(snapshot.PlanType == "pro", "plan comes from rateLimits envelope");
Assert(!snapshot.Windows.Any(x => x.Id.Contains("secondary", StringComparison.Ordinal) || x.Id.Contains("individual", StringComparison.Ordinal)), "null secondary and individual limits do not crash or fabricate windows");
Assert(snapshot.Credits == "Credits: 18.5" && snapshot.ResetCredits == "Reset credits: 2", "credits are human normalized");
Assert(snapshot.Windows.Count == 2, "historical codex bucket deduplicates and model bucket remains");
Assert(snapshot.Windows.Any(x => x.Id == "codex:primary" && x.ResetsAt == DateTimeOffset.FromUnixTimeSeconds(1786550400)), "unix reset parses");
Assert(snapshot.Windows.Any(x => x.Label.StartsWith("GPT-5.3 Codex Spark", StringComparison.Ordinal)), "model bucket keeps parent name");
var sparse = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "primary": { "usedPercent":55,"windowDurationMins":300 } }, "rateLimitsByLimitId": { "codex_bengalfox": { "limitName":"GPT-5.3 Codex Spark", "primary":{"usedPercent":70,"windowDurationMins":10080} } } }""").RootElement, DateTimeOffset.UtcNow);
var merged = RateLimitParser.Merge(snapshot, sparse);
Assert(merged.PlanType == "pro" && merged.Credits == "Credits: 18.5" && merged.Windows.Count == 2, "sparse update preserves metadata and buckets");
var weeklyOnly = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "planType":"pro", "primary":{"usedPercent":9,"windowDurationMins":10080} } }""").RootElement, DateTimeOffset.UtcNow);
Assert(!weeklyOnly.Windows.Any(x => x.WindowDurationMins is >= 240 and <= 360), "missing five-hour window is absent rather than zero");
var codexOnlyByLimitId = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimitsByLimitId": { "codex": { "primary":{"usedPercent":17,"windowDurationMins":300}, "secondary":{"usedPercent":41,"windowDurationMins":10080} } } }""").RootElement, DateTimeOffset.UtcNow);
Assert(codexOnlyByLimitId.Windows.Count == 2 && OfficialCodexQuotaWindows.FiveHours(codexOnlyByLimitId.Windows)?.Id == "codex:primary" && OfficialCodexQuotaWindows.Weekly(codexOnlyByLimitId.Windows)?.Id == "codex:secondary", "canonical by-limit codex bucket is a fallback when the legacy envelope is absent and roles may be reversed");
var rootAndDuplicateCodex = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "primary":{"usedPercent":23,"windowDurationMins":300}, "secondary":{"usedPercent":55,"windowDurationMins":10080} }, "rateLimitsByLimitId": { "codex": { "primary":{"usedPercent":99,"windowDurationMins":300}, "secondary":{"usedPercent":99,"windowDurationMins":10080} } } }""").RootElement, DateTimeOffset.UtcNow);
Assert(rootAndDuplicateCodex.Windows.Count == 2 && rootAndDuplicateCodex.Windows.All(window => window.UsedPercent != 99), "legacy envelope readings win over duplicate canonical codex fields");
var genericAndFamilyLimits = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "primary":{"usedPercent":51,"windowDurationMins":10080}, "secondary":null }, "rateLimitsByLimitId": { "codex": { "primary":{"usedPercent":51,"windowDurationMins":10080} }, "codex_bengalfox": { "limitName":"GPT-5.3 Codex Spark", "primary":{"usedPercent":13,"windowDurationMins":300}, "secondary":{"usedPercent":31,"windowDurationMins":10080} } } }""").RootElement, DateTimeOffset.UtcNow);
Assert(OfficialCodexQuotaWindows.FiveHours(genericAndFamilyLimits.Windows) is null && OfficialCodexQuotaWindows.Weekly(genericAndFamilyLimits.Windows)?.UsedPercent == 51, "generic indicators exclude model-family five-hour and weekly buckets even when their roles contain both durations");
var compactQuotaViewModel = new MainViewModel();
compactQuotaViewModel.ApplyQuota(new RateLimitSnapshot(codexOnlyByLimitId.Windows, null, null, null, DateTimeOffset.UtcNow));
Assert(compactQuotaViewModel.HasFiveHourQuota && compactQuotaViewModel.FiveHour == "83%" && compactQuotaViewModel.Weekly == "59%" && compactQuotaViewModel.ShowCompactFiveHour && compactQuotaViewModel.ShowCompactWeekly, "both compact gauges default to official five-hour and weekly durations independently of primary or secondary roles");
compactQuotaViewModel.SetCompactQuotaDisplay("5h");
Assert(compactQuotaViewModel.ShowCompactFiveHour && !compactQuotaViewModel.ShowCompactWeekly, "five-hour compact selection leaves only the detected five-hour gauge visible");
compactQuotaViewModel.SetCompactQuotaDisplay("7d");
Assert(!compactQuotaViewModel.ShowCompactFiveHour && compactQuotaViewModel.ShowCompactWeekly, "seven-day compact selection leaves only the weekly gauge visible");
compactQuotaViewModel.ApplyQuota(new RateLimitSnapshot(weeklyOnly.Windows, null, null, null, DateTimeOffset.UtcNow));
compactQuotaViewModel.SetCompactQuotaDisplay("5h");
Assert(!compactQuotaViewModel.HasFiveHourQuota && !compactQuotaViewModel.ShowCompactFiveHour, "selecting five-hour without a detected official window never fabricates a gauge");
var officialRemainingFixture = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "primary":{"usedPercent":16,"resetsAt":1787090315,"windowDurationMins":10080} }, "rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":16,"windowDurationMins":10080}}} }""").RootElement, DateTimeOffset.UtcNow);
var officialWeekly = officialRemainingFixture.Windows.Single(x => x.Id == "codex:primary");
Assert(QuotaPresentation.FormatWeeklyRemaining(officialWeekly) == "84%", "official used=16 displays 84% remaining to match Codex UI");
Assert(QuotaPresentation.FormatWeeklyRemaining(new("codex:primary", "Weekly", 0, null, 10080)) == "100%", "full remaining quota stays legible in compact display");
Assert(CircularGaugeMath.Clamp(-1) == 0 && CircularGaugeMath.Clamp(101) == 100, "gauge clamps values to its 0-100 range");
Assert(CircularGaugeMath.SweepAngle(50) == 180 && CircularGaugeMath.SweepAngle(83) == 298.8, "gauge sweep follows remaining percent");
Assert(CircularGaugeMath.IsFullCircle(100) && !CircularGaugeMath.IsFullCircle(99.5), "gauge keeps a distinct full-circle path");
Assert(WeeklyForecastCalculator.Calculate(officialWeekly, DateTimeOffset.UtcNow).Status is not null, "forecast keeps internal usedPercent semantics");
var numericCredits = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "credits": { "balance": 2.75, "hasCredits": true } } }""").RootElement, DateTimeOffset.UtcNow);
Assert(numericCredits.Credits == "Credits: 2.75", "numeric credit balance remains supported");
var nullCredits = RateLimitParser.Parse(JsonDocument.Parse("""{ "rateLimits": { "credits": { "balance": null, "hasCredits": false } } }""").RootElement, DateTimeOffset.UtcNow);
Assert(nullCredits.Credits == "Credits: unavailable", "null balance with no credits is explicit");
Assert(ResetCountdown.Format(DateTimeOffset.UtcNow.AddMinutes(32), DateTimeOffset.UtcNow).Contains("32m"), "countdown formats minutes");
Assert(ResetCountdown.Format(null, DateTimeOffset.UtcNow) == "reset indisponível", "countdown localizes unavailable");
var portugueseResetAt = new DateTimeOffset(2026, 9, 18, 21, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 18, 21, 0, 0)));
Assert(ResetCountdown.Format(DateTimeOffset.UtcNow.AddHours(6).AddMinutes(7), DateTimeOffset.UtcNow).StartsWith("reinicia em 6h"), "countdown localizes reset");
Assert(ResetCountdown.Format(portugueseResetAt, portugueseResetAt.AddHours(-1)) == "reinicia em 1h 0m (18/09 as 21:00h)", "countdown includes the local absolute reset date and time");
Assert(CodexExecutableDiscovery.Candidates("C:\\custom\\codex.exe", "C:\\user").First() == "C:\\custom\\codex.exe", "configured path wins");
var nowForecast = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
var defaultUsageRoots = LocalUsageAnalyticsService.DefaultRoots("C:\\profile");
Assert(defaultUsageRoots.Count == 2 && defaultUsageRoots[0].EndsWith(".codex\\sessions") && defaultUsageRoots[1].EndsWith(".codex\\archived_sessions"), "default analytics includes active and archived Codex sessions");
Assert(CurrencyPresentation.Normalize("brl") == "BRL", "BRL currency code normalizes to BRL");
Assert(CurrencyPresentation.Normalize("usd") == "USD", "USD currency code normalizes to USD");
Assert(CurrencyPresentation.Normalize("EUR") == "BRL", "unsupported currency falls back to BRL");
Assert(CurrencyPresentation.FormatMonthlyCost(3.2m, 17.6m, "BRL") == "R$ 17,60", "BRL cost formats the analytics value converted with the configured rate");
Assert(CurrencyPresentation.FormatMonthlyCost(3.2m, 17.6m, "USD") == "US$ 3,20", "USD cost uses the base value without conversion");
Assert(CurrencyPresentation.FormatCost(.8m, 4.4m, "BRL") == "R$ 4,40", "BRL formats today cost");
Assert(CurrencyPresentation.FormatCost(.8m, 4.4m, "USD") == "US$ 0,80", "USD formats today cost without conversion");
var currencyViewModel = new MainViewModel();
var currencyReset = DateTimeOffset.UtcNow.AddDays(1);
var currencyTimelineAt = currencyReset.AddHours(-1);
var currencyAnalytics = new UsageAnalytics(10, 20, 2m, 10m, 100, [], .5m, 2.5m, [new(DateTime.Today, 10, .5m, 2.5m)], [new(currencyTimelineAt, 10, 1m)], 5m);
currencyViewModel.Apply(new RateLimitSnapshot([new("codex:primary", "Weekly", 10, currencyReset, 10080)], null, null, null, DateTimeOffset.UtcNow), currencyAnalytics, "BRL");
Assert(currencyViewModel.WeeklyCost == "R$ 5,00" && currencyViewModel.TodayCost == "R$ 2,50" && currencyViewModel.MonthCost == "R$ 10,00", "view model initially formats all retained costs in BRL");
currencyViewModel.SetCurrency("USD");
Assert(currencyViewModel.CurrencyCode == "USD" && currencyViewModel.WeeklyCost == "US$ 1,00" && currencyViewModel.TodayCost == "US$ 0,50" && currencyViewModel.MonthCost == "US$ 2,00", "currency change immediately reformats retained weekly, daily and monthly costs without analytics refresh");
var forecastViewModel = new MainViewModel();
var currentForecast = DateTimeOffset.Now;
forecastViewModel.ApplyQuota(new RateLimitSnapshot([new("codex:primary", "Weekly", 80, currentForecast.AddDays(6), 10080)], null, null, null, currentForecast));
Assert(forecastViewModel.Reset.StartsWith("reinicia em ", StringComparison.Ordinal) && !forecastViewModel.Reset.Contains("restante esta semana", StringComparison.OrdinalIgnoreCase), "weekly reset keeps only the reset countdown label");
Assert(forecastViewModel.IsExhaustionRisk && forecastViewModel.Forecast.StartsWith("Risco de esgotar antes do reset", StringComparison.Ordinal), "view model exposes the early exhaustion risk for conditional UI emphasis");
forecastViewModel.ApplyQuota(new RateLimitSnapshot([new("codex:primary", "Weekly", 10, currentForecast.AddDays(3), 10080)], null, null, null, currentForecast));
Assert(!forecastViewModel.IsExhaustionRisk, "forecast emphasis clears when quota should last until reset");
Assert(TokenPresentation.Format(999) == "999", "small token counts remain legible");
Assert(TokenPresentation.Format(1_000) == "1 mil", "one thousand tokens uses mil");
Assert(TokenPresentation.Format(1_000_000) == "1 mi", "one million tokens uses mi");
Assert(TokenPresentation.Format(1_000_000_000) == "1 bi", "one billion tokens uses bi");
Assert(TokenPresentation.Format(1_234_567) == "1,23 mi", "abbreviated tokens round to two decimal places in pt-BR");
var duration = 10080;
var durable = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, nowForecast.AddDays(3), duration), nowForecast);
Assert(durable.Status == "Deve durar até o reset" && NearlyEqual(durable.ProjectedPercent, 17.5) && durable.ExhaustsAt is null, "forecast reports exact durable projection");
var risky = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 80, nowForecast.AddDays(6), duration), nowForecast);
Assert(risky.Status == "Risco de esgotar antes do reset" && NearlyEqual(risky.ProjectedPercent, 560) && risky.ExhaustsAt == nowForecast.AddHours(6), "forecast reports exact early exhaustion");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 0, nowForecast.AddDays(3), duration), nowForecast).Status == "Dados insuficientes", "forecast handles zero usage");
var observed = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 23, nowForecast.AddDays(6).AddHours(5).AddMinutes(24), duration), nowForecast);
Assert(NearlyEqual(observed.ProjectedPercent, 207.741935483871) && NearlyEqualInstant(observed.ExhaustsAt, new DateTimeOffset(2026, 8, 15, 2, 16, 10, 435, TimeSpan.Zero)), "forecast reproduces the observed 208 percent risk and exhaustion instant");
var exhausted = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 100, nowForecast.AddDays(6), duration), nowForecast);
Assert(exhausted.Status == "Limite esgotado" && exhausted.ExhaustsAt == nowForecast, "forecast explicitly reports an exhausted quota");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", double.NaN, nowForecast.AddDays(6), duration), nowForecast).Status == "Dados insuficientes", "forecast rejects non-finite usage");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, null, duration), nowForecast).Status == "Dados insuficientes", "forecast rejects a missing reset");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, nowForecast.AddDays(6), null), nowForecast).Status == "Dados insuficientes", "forecast rejects a missing duration");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, nowForecast, duration), nowForecast).Status == "Dados insuficientes", "forecast rejects an elapsed reset");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, nowForecast.AddDays(7).AddSeconds(1), duration), nowForecast).Status == "Dados insuficientes", "forecast rejects a future window start");
Assert(WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 10, nowForecast.AddDays(7).AddMinutes(-1), duration), nowForecast).Status == "Dados insuficientes", "forecast rejects a one-minute sample");
var offsetNow = nowForecast.ToOffset(TimeSpan.FromHours(-3));
var utcForecast = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 23, nowForecast.AddDays(6), duration), nowForecast);
var localForecast = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 23, nowForecast.AddDays(6), duration), offsetNow);
Assert(localForecast.ProjectedPercent is double localProjected && NearlyEqual(utcForecast.ProjectedPercent, localProjected) && utcForecast.ExhaustsAt == localForecast.ExhaustsAt, "forecast is invariant across local and UTC offsets");
var roundedBoundary = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 50.02, nowForecast.AddDays(3.5), duration), nowForecast);
Assert(roundedBoundary.Status == "Deve durar até o reset" && WeeklyForecastCalculator.FormatProjectedPercent(roundedBoundary.ProjectedPercent!.Value) == "100,0%", "rounded 100 percent projection cannot contradict its status");
var roundedRisk = WeeklyForecastCalculator.Calculate(new("codex:primary", "Weekly", 50.245, nowForecast.AddDays(3.5), duration), nowForecast);
Assert(roundedRisk.Status == "Risco de esgotar antes do reset" && WeeklyForecastCalculator.FormatProjectedPercent(roundedRisk.ProjectedPercent!.Value) == "100,5%", "near-threshold risk keeps one decimal place without contradicting its status");
var mergeReset = nowForecast.AddDays(6);
var mergeCurrent = new RateLimitSnapshot([new("codex:primary", "Weekly", 23, mergeReset, duration)], "pro", null, null, nowForecast);
var sameCycleSparse = new RateLimitSnapshot([new("codex:primary", "Usage limit", 24, null, null)], null, null, null, nowForecast.AddMinutes(1), true);
var sameCycleWindow = RateLimitParser.Merge(mergeCurrent, sameCycleSparse).Windows.Single();
Assert(sameCycleWindow.UsedPercent == 24 && sameCycleWindow.ResetsAt == mergeReset && sameCycleWindow.WindowDurationMins == duration, "same-cycle sparse update preserves forecast timing");
var newCycleSparse = new RateLimitSnapshot([new("codex:primary", "Usage limit", 2, null, null)], null, null, null, nowForecast.AddMinutes(2), true);
var newCycleWindow = RateLimitParser.Merge(mergeCurrent, newCycleSparse).Windows.Single();
Assert(newCycleWindow.UsedPercent == 2 && newCycleWindow.ResetsAt is null && newCycleWindow.WindowDurationMins is null, "usage drop never inherits timing from the previous cycle");
var elapsedTimingSparse = new RateLimitSnapshot([new("codex:primary", "Usage limit", 24, null, null)], null, null, null, mergeReset.AddSeconds(1), true);
var elapsedTimingWindow = RateLimitParser.Merge(mergeCurrent, elapsedTimingSparse).Windows.Single();
Assert(elapsedTimingWindow.ResetsAt is null && elapsedTimingWindow.WindowDurationMins is null, "sparse update never inherits an elapsed reset");
var analyticsRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-analytics-" + Guid.NewGuid());
var gitResolverRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-git-roots-" + Guid.NewGuid());
var repoRoot = Path.Combine(gitResolverRoot, "repo-a"); var repoSubdir = Path.Combine(repoRoot, "src", "nested");
Directory.CreateDirectory(Path.Combine(repoRoot, ".git")); Directory.CreateDirectory(repoSubdir);
var worktreeRoot = Path.Combine(gitResolverRoot, "worktree"); var worktreeGit = Path.Combine(gitResolverRoot, "metadata", "worktrees", "worktree"); var commonGit = Path.Combine(gitResolverRoot, ".git");
Directory.CreateDirectory(worktreeRoot); Directory.CreateDirectory(worktreeGit); Directory.CreateDirectory(commonGit);
File.WriteAllText(Path.Combine(worktreeRoot, ".git"), "gitdir: " + worktreeGit); File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../../../.git");
var nonGitRoot = gitResolverRoot + "-plain"; Directory.CreateDirectory(nonGitRoot);
var invalidGitRoot = gitResolverRoot + "-invalid"; Directory.CreateDirectory(invalidGitRoot); File.WriteAllText(Path.Combine(invalidGitRoot, ".git"), "not a gitdir");
var invalidCommonRoot = gitResolverRoot + "-invalid-common"; var invalidCommonGit = Path.Combine(gitResolverRoot, "metadata", "worktrees", "invalid-common"); Directory.CreateDirectory(invalidCommonRoot); Directory.CreateDirectory(invalidCommonGit); File.WriteAllText(Path.Combine(invalidCommonRoot, ".git"), "gitdir: " + invalidCommonGit); File.WriteAllText(Path.Combine(invalidCommonGit, "commondir"), "../../missing.git");
var sameNameA = Path.Combine(gitResolverRoot, "left", "same-name"); var sameNameB = Path.Combine(gitResolverRoot, "right", "same-name"); Directory.CreateDirectory(Path.Combine(sameNameA, ".git")); Directory.CreateDirectory(Path.Combine(sameNameB, ".git"));
var projectResolver = new ProjectRootResolver();
Assert(projectResolver.Resolve(repoSubdir) == Path.GetFullPath(repoRoot) && projectResolver.Resolve(worktreeRoot) == Path.GetFullPath(gitResolverRoot) && projectResolver.Resolve(Path.Combine(gitResolverRoot, "missing")) is null && projectResolver.Resolve(nonGitRoot) is null && projectResolver.Resolve(invalidGitRoot) is null && projectResolver.Resolve(invalidCommonRoot) is null && !string.Equals(projectResolver.Resolve(sameNameA), projectResolver.Resolve(sameNameB), StringComparison.OrdinalIgnoreCase), "Git project resolver accepts directory and linked-worktree markers, rejects missing/non-Git/invalid metadata including invalid commondir, and preserves distinct same-basename roots");
Directory.Delete(gitResolverRoot, true);
Directory.Delete(nonGitRoot, true); Directory.Delete(invalidGitRoot, true); Directory.Delete(invalidCommonRoot, true);
Directory.CreateDirectory(analyticsRoot);
File.WriteAllText(Path.Combine(analyticsRoot, "session.jsonl"), """
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-terra"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":0,"total_tokens":100}}}}
{"timestamp":"2026-08-12T10:01:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":150,"cached_input_tokens":30,"output_tokens":0,"total_tokens":150}}}}
{"timestamp":"2026-08-12T10:02:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":180,"cached_input_tokens":30,"output_tokens":0,"total_tokens":180}}}}
malformed
""" + Environment.NewLine);
var analyticsNow = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
var analyticsService = new LocalUsageAnalyticsService(() => analyticsNow);
var analytics = analyticsService.Read(5.5m, analyticsRoot);
var analyticsReferenceService = new LocalUsageAnalyticsService(() => analyticsNow, maxParseParallelism: 1);
var analyticsReference = analyticsReferenceService.Read(5.5m, analyticsRoot);
Assert(analytics.MonthTokens == 180, "cumulative token snapshots use deltas, never 430");
Assert(analytics.MonthTokens == analyticsReference.MonthTokens && analytics.TodayTokens == analyticsReference.TodayTokens && analytics.MonthUsd == analyticsReference.MonthUsd && analytics.TodayUsd == analyticsReference.TodayUsd && analyticsService.FilesParsedLastRead == analyticsReferenceService.FilesParsedLastRead && analyticsService.FilesRebuiltLastRead == analyticsReferenceService.FilesRebuiltLastRead && analyticsService.BytesReadLastRead == analyticsReferenceService.BytesReadLastRead && analyticsService.FilesParsedLastRead == 1 && analyticsService.FilesRebuiltLastRead == 1 && analyticsService.BytesReadLastRead > 0, "bounded-parallel and sequential cold analytics preserve identical results and counters");
Assert(analytics.TodayUsd == 0.0003825m && analytics.TodayBrl == 0.00210375m, "today cost uses the same priced token buckets and configured BRL rate as the month");
var dailySeries = analytics.DailySeries ?? throw new InvalidOperationException("daily series missing");
Assert(dailySeries.Count == 31 && dailySeries.Sum(x => x.Tokens) == analytics.MonthTokens, "current-month chart includes every calendar day and sums exactly to month usage");
Assert(dailySeries.Count(x => x.Tokens == 0) == 30 && dailySeries.Single(x => x.Day.Day == 12).Tokens == 180, "daily chart explicitly fills zero-usage days");
Assert(dailySeries.Sum(x => x.UsdCost) == analytics.MonthUsd && dailySeries.Sum(x => x.BrlCost) == analytics.MonthBrl, "daily chart costs sum exactly to monthly USD and BRL estimates");
var exactReset = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);
var boundaryAnalytics = new UsageAnalytics(0, 0, 0, 0, 0, [], Timeline:
[
    new(exactReset.AddDays(-7).AddTicks(-1), 1, .01m),
    new(exactReset.AddDays(-7), 2, .02m),
    new(exactReset.AddMinutes(-1), 4, .04m),
    new(exactReset, 8, .08m)
], UsdBrl: 5m);
Assert(boundaryAnalytics.TokensInWindow(exactReset.AddDays(-7), exactReset) == 6, "weekly tokens use the exact inclusive start and exclusive official reset boundaries");
var boundaryEstimate = boundaryAnalytics.EstimateInWindow(exactReset.AddDays(-7), exactReset);
Assert(boundaryEstimate == new UsageWindowEstimate(6, .06m, .30m), "weekly cost uses the same exact event boundaries as weekly tokens");
Assert(CurrencyPresentation.FormatCost(boundaryEstimate.CostUsd, boundaryEstimate.CostBrl, "USD") == "US$ 0,06" && CurrencyPresentation.FormatCost(boundaryEstimate.CostUsd, boundaryEstimate.CostBrl, "BRL") == "R$ 0,30", "weekly estimate formats in the selected USD or BRL currency");
var offsetReset = exactReset.ToOffset(TimeSpan.FromHours(-3));
Assert(boundaryAnalytics.TokensInWindow(offsetReset.AddDays(-7), offsetReset) == 6, "weekly interval is invariant when the official reset is represented with another timezone offset");
Assert(boundaryAnalytics.TokensInWeeklyWindow(new("codex:primary", "Weekly", 50, offsetReset, 10080)) == 6, "weekly token counter integrates the official quota reset rather than calendar-day approximations");
Assert(boundaryAnalytics.TokensInWeeklyWindow(new("codex:primary", "Weekly", 50, null, 10080)) is null, "weekly token counter stays unavailable without an official reset boundary");
var retentionRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-timeline-retention-" + Guid.NewGuid());
Directory.CreateDirectory(retentionRoot);
var recentAt = DateTimeOffset.UtcNow.AddHours(-1);
var oldAt = DateTimeOffset.UtcNow.AddDays(-20);
File.WriteAllText(Path.Combine(retentionRoot, "retention.jsonl"),
    $"{{\"timestamp\":\"{oldAt:O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":10,\"total_tokens\":10}}}}}}}}\n" +
    $"{{\"timestamp\":\"{recentAt:O}\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"total_token_usage\":{{\"input_tokens\":20,\"total_tokens\":20}}}}}}}}\n");
var retentionAnalytics = new LocalUsageAnalyticsService();
var retainedUsage = retentionAnalytics.Read(5.5m, retentionRoot);
Assert(retainedUsage.Timeline is { Count: 1 } && retainedUsage.Timeline[0].Tokens == 10 && retainedUsage.Timeline[0].CostUsd == 0, "exact timeline retains recent deltas while unknown models remain excluded from estimated cost");
Assert(retentionAnalytics.CachedTimelineEntryCount == 1, "cached timeline remains bounded independently of historical session age");
Directory.Delete(retentionRoot, true);
var componentRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-components-" + Guid.NewGuid());
Directory.CreateDirectory(componentRoot);
File.WriteAllText(Path.Combine(componentRoot, "components.jsonl"), """
{"type":"session_meta","payload":{"session_id":"components","id":"components","thread_source":"root"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-sol"}}
{"timestamp":"2026-08-12T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":80,"output_tokens":10,"reasoning_output_tokens":5,"total_tokens":110}}}}
""" + Environment.NewLine);
var componentUsage = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, componentRoot);
Assert(componentUsage.MonthTokens == 110, "processed tokens equal input plus output because reasoning is an output subset");
Assert(componentUsage.MonthUsd == 0.00044m, "reasoning tokens use the output tariff while cached input and reasoning remain mutually exclusive tooltip categories");
var componentBreakdown = componentUsage.DailySeries!.Single(day => day.Day.Day == 12).Breakdown!;
Assert(componentBreakdown.CachedReadTokens == 80 && componentBreakdown.InputTokens == 20 && componentBreakdown.OutputTokens == 5 && componentBreakdown.ReasoningTokens == 5 && componentBreakdown.TotalTokens == 110 && componentBreakdown.TotalCostUsd == componentUsage.MonthUsd, "token breakdown excludes cached read from input and reasoning from output while its categories sum to the displayed total and cost");
Directory.Delete(componentRoot, true);
var chatUsageRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-chat-usage-" + Guid.NewGuid());
Directory.CreateDirectory(chatUsageRoot);
File.WriteAllText(Path.Combine(chatUsageRoot, "root.jsonl"), """
{"type":"session_meta","payload":{"session_id":"root-session","id":"root-chat","cwd":"C:\\work\\project-alpha"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-sol"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":10,"reasoning_output_tokens":5}}}}
""" + Environment.NewLine);
File.WriteAllText(Path.Combine(chatUsageRoot, "child.jsonl"), """
{"type":"session_meta","payload":{"session_id":"child-session","id":"child-chat","parent_thread_id":"root-chat","thread_source":"subagent","cwd":"C:\\work\\project-alpha"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"unknown-model"}}
{"timestamp":"2026-08-12T10:05:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":50,"cached_input_tokens":10,"output_tokens":0}}}}
""" + Environment.NewLine);
File.WriteAllText(Path.Combine(chatUsageRoot, "same-title.jsonl"), """
{"type":"session_meta","payload":{"session_id":"other-session","id":"other-chat","cwd":"C:\\work\\project-alpha"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-terra"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":25,"output_tokens":0}}}}
""" + Environment.NewLine);
var chatUsage = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, chatUsageRoot);
var consolidatedChat = chatUsage.Chats!.Single(chat => chat.ThreadId == "root-chat");
Assert(chatUsage.Chats!.Count == 2 && consolidatedChat.Tokens == 160 && consolidatedChat.Breakdown.TotalTokens == 160 && consolidatedChat.PricedTokens == 110 && consolidatedChat.CostUsd == consolidatedChat.Breakdown.TotalCostUsd && consolidatedChat.LastUpdatedAt == new DateTimeOffset(2026, 8, 12, 10, 5, 0, TimeSpan.Zero), "explicit child usage consolidates once into its root chat while independently rooted chats retain the root flow's latest real usage timestamp");
Assert(consolidatedChat.ProjectPath is null && consolidatedChat.Breakdown.CachedReadTokens + consolidatedChat.Breakdown.InputTokens + consolidatedChat.Breakdown.OutputTokens + consolidatedChat.Breakdown.ReasoningTokens == consolidatedChat.Tokens, "monthly chat snapshot rejects an unverifiable cwd while token categories continue to close exactly");
var chatDetailsViewModel = new MainViewModel();
var chatDetailsSnapshot = new RateLimitSnapshot([new("codex:primary", "Weekly", 20, analyticsNow.AddDays(4), 10080)], null, null, null, analyticsNow);
chatDetailsViewModel.Apply(chatDetailsSnapshot, new UsageAnalytics(0, 185, .01m, .055m, 50, [], UsdBrl: 5.5m, Chats:
[
    consolidatedChat with { ProjectPath = @"C:\work\project-alpha" },
    new ChatUsage("missing-cwd", null, "Repeated title", 25, .0001m, 25, new TokenUsageBreakdown(0, 25, 0, 0, 0, .0001m), analyticsNow.AddMinutes(-3)),
    new ChatUsage("same-basename-other-root", @"D:\other\project-alpha", "Same basename", 1, .00001m, 1, new TokenUsageBreakdown(0, 1, 0, 0, 0, .00001m), analyticsNow.AddMinutes(-2)),
    new ChatUsage("same-casing-root", @"C:\WORK\PROJECT-ALPHA", "Same path casing", 1, .00001m, 1, new TokenUsageBreakdown(0, 1, 0, 0, 0, .00001m), analyticsNow.AddMinutes(-1))
]), "BRL");
Assert(chatDetailsViewModel.ChatProjects.Count == 3 && chatDetailsViewModel.ChatProjects.Count(project => project.Project == "project-alpha") == 2 && chatDetailsViewModel.ChatProjects.All(project => !project.IsExpanded && project.Chats.Count == 0) && chatDetailsViewModel.ChatProjects.All(project => !project.Project.Contains(@"C:\work", StringComparison.OrdinalIgnoreCase) && !project.Project.Contains(@"D:\other", StringComparison.OrdinalIgnoreCase)), "chat details use the complete normalized cwd as a case-insensitive hidden grouping identity, initially collapse every project, and never render local paths");
var primaryAlpha = chatDetailsViewModel.ChatProjects.First(project => project.Project == "project-alpha");
primaryAlpha.Toggle();
var rootChatRow = primaryAlpha.Chats.Single(row => row.Title == LocalizationManager.Text("CodexConversation"));
Assert(primaryAlpha.Chats.Count == 2 && rootChatRow.EstimateNote == LocalizationManager.Text("PartialTariffEstimate") && rootChatRow.CachedReadFraction == 30d / 160d && rootChatRow.InputFraction == 120d / 160d && rootChatRow.OutputFraction == 5d / 160d && rootChatRow.ReasoningFraction == 5d / 160d && rootChatRow.TotalFraction == 1d, "expanding a project materializes only its chats with exclusive category ratios over the chat total");
var chatOrderingViewModel = new MainViewModel();
chatOrderingViewModel.Apply(chatDetailsSnapshot, new UsageAnalytics(0, 101, 0, 0, 0, [], UsdBrl: 5.5m, Chats:
[
    new ChatUsage("older-high", @"C:\work\ordered", "Mais antigo", 100, 0, 0, TokenUsageBreakdown.Zero, analyticsNow.AddMinutes(-10)),
    new ChatUsage("newer-low", @"C:\work\ordered", "Mais recente", 1, 0, 0, TokenUsageBreakdown.Zero, analyticsNow.AddMinutes(-1)),
    new ChatUsage("newer-low-a", @"C:\work\ordered", "A desempate", 1, 0, 0, TokenUsageBreakdown.Zero, analyticsNow.AddMinutes(-1))
]), "BRL");
var orderedProject = chatOrderingViewModel.ChatProjects.Single(); orderedProject.Toggle();
Assert(orderedProject.Chats.Select(chat => chat.Title).SequenceEqual(["A desempate", "Mais recente", "Mais antigo"]), "details by chat orders latest usage updates before token volume, then uses title and thread id as deterministic tie-breakers");
chatDetailsViewModel.ChatSearch = "PROJECT-ALPHA";
Assert(chatDetailsViewModel.ChatProjects.Count(project => project.IsVisible) == 2 && chatDetailsViewModel.ChatProjects.Where(project => project.IsVisible).All(project => !project.IsExpanded && project.Chats.Count == 0), "case-insensitive project search shows matching groups without autoexpanding or materializing chats");
chatDetailsViewModel.ChatProjects.First(project => project.IsVisible).Toggle();
Assert(chatDetailsViewModel.ChatProjects.First(project => project.IsVisible).Chats.Count > 0, "manual expansion after project search materializes only that filtered project's chats");
chatDetailsViewModel.ChatSearch = "rePeAtEd TiTlE";
Assert(chatDetailsViewModel.ChatProjects.Count(project => project.IsVisible) == 1 && !chatDetailsViewModel.ChatProjects.Single(project => project.IsVisible).IsExpanded && chatDetailsViewModel.ChatProjects.Single(project => project.IsVisible).Chats.Count == 0, "case-insensitive chat search shows only the matching project while keeping its results lazy");
chatDetailsViewModel.ChatProjects.Single(project => project.IsVisible).Toggle();
Assert(chatDetailsViewModel.ChatProjects.Single(project => project.IsVisible).Chats.Single().Title == "Repeated title", "manual expansion after chat search materializes only matching chats");
chatDetailsViewModel.ChatSearch = "";
Assert(chatDetailsViewModel.HasVisibleChatProjects && chatDetailsViewModel.ChatProjects.All(project => !project.IsExpanded && project.Chats.Count == 0), "clearing chat search restores collapsed groups, releases materialized chat rows, and restores the visible project list");
chatDetailsViewModel.ChatSearch = "no matching chat";
Assert(!chatDetailsViewModel.HasVisibleChatProjects && chatDetailsViewModel.ChatProjects.All(project => !project.IsVisible), "a zero-match chat search exposes the localized empty-state condition instead of an empty list area");
chatDetailsViewModel.ChatSearch = "";
primaryAlpha.Toggle();
chatDetailsViewModel.Apply(chatDetailsSnapshot, new UsageAnalytics(0, 185, .01m, .055m, 50, [], UsdBrl: 5.5m, Chats:
[
    consolidatedChat,
    new ChatUsage("missing-cwd", null, "Repeated title", 25, .0001m, 25, new TokenUsageBreakdown(0, 25, 0, 0, 0, .0001m), analyticsNow.AddMinutes(-3)),
    new ChatUsage("same-basename-other-root", @"D:\other\project-alpha", "Same basename", 1, .00001m, 1, new TokenUsageBreakdown(0, 1, 0, 0, 0, .00001m), analyticsNow.AddMinutes(-2)),
    new ChatUsage("same-casing-root", @"C:\WORK\PROJECT-ALPHA", "Same path casing", 1, .00001m, 1, new TokenUsageBreakdown(0, 1, 0, 0, 0, .00001m), analyticsNow.AddMinutes(-1))
]), "BRL");
Assert(chatDetailsViewModel.ChatProjects.First(project => project.Key.Equals(primaryAlpha.Key, StringComparison.OrdinalIgnoreCase)).IsExpanded, "analytics refresh preserves a manually expanded project by its hidden normalized project key");
chatDetailsViewModel.ChatSearch = "repeated title";
chatDetailsViewModel.Apply(chatDetailsSnapshot, new UsageAnalytics(0, 25, .0001m, .00055m, 100, [], UsdBrl: 5.5m, Chats:
[
    new ChatUsage("missing-cwd", null, "Repeated title", 25, .0001m, 25, new TokenUsageBreakdown(0, 25, 0, 0, 0, .0001m), analyticsNow.AddMinutes(-3))
]), "BRL");
Assert(chatDetailsViewModel.HasVisibleChatProjects && !chatDetailsViewModel.ChatProjects.Single().IsExpanded && chatDetailsViewModel.ChatProjects.Single().Chats.Count == 0, "an active search remains applied without autoexpanding or materializing projects after analytics refresh");
chatDetailsViewModel.ChatProjects.Single().Toggle();
Assert(chatDetailsViewModel.ChatProjects.Single().Chats.Single().Title == "Repeated title", "manual expansion after refresh still materializes the active search match");
chatDetailsViewModel.ResetChatDetailsView();
Assert(chatDetailsViewModel.ChatSearch == "" && chatDetailsViewModel.HasVisibleChatProjects && chatDetailsViewModel.ChatProjects.All(project => project.IsVisible && !project.IsExpanded && project.Chats.Count == 0), "resetting the chat-details view clears search and restores all projects to their collapsed lazy initial state");
var zeroChatViewModel = new MainViewModel();
zeroChatViewModel.Apply(chatDetailsSnapshot, new UsageAnalytics(0, 0, 0, 0, 0, [], UsdBrl: 5.5m, Chats:
[
    new ChatUsage("zero", null, "Zero", 0, 0, 0, TokenUsageBreakdown.Zero, analyticsNow)
]), "BRL");
var zeroProject = zeroChatViewModel.ChatProjects.Single(); zeroProject.Toggle();
Assert(zeroProject.Chats.Single().CachedReadFraction == 0 && zeroProject.Chats.Single().InputFraction == 0 && zeroProject.Chats.Single().OutputFraction == 0 && zeroProject.Chats.Single().ReasoningFraction == 0 && zeroProject.Chats.Single().TotalFraction == 0, "zero-token chat ratios remain finite and the total bar is zero when there is no total");
var lockedRolloutDatabase = Path.Combine(chatUsageRoot, "locked-rollout.sqlite");
using (var connection = new SqliteConnection("Data Source=" + lockedRolloutDatabase))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT); INSERT INTO threads (id, title) VALUES ('locked-chat', 'Rollout aberto');";
    command.ExecuteNonQuery();
}
var lockedRolloutPath = Path.Combine(chatUsageRoot, "locked-rollout.jsonl");
File.WriteAllText(lockedRolloutPath, """
{"timestamp":"2026-08-12T10:00:00Z","type":"session_meta","payload":{"session_id":"locked-session","id":"locked-chat"}}
{"timestamp":"2026-08-12T10:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":2,"reasoning_output_tokens":0}}}}
""" + Environment.NewLine);
using (var lockedWriter = new FileStream(lockedRolloutPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
{
    var lockedRollout = new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: lockedRolloutDatabase).Read(5.5m, chatUsageRoot).Chats!.Single(chat => chat.ThreadId == "locked-chat");
    Assert(lockedRollout.Title == "Rollout aberto" && lockedRollout.Tokens == 12, "an actively writable rollout preserves its metadata identity, title, and token usage");
}
var titleOnlyDatabase = Path.Combine(chatUsageRoot, "title-only.sqlite");
using (var connection = new SqliteConnection("Data Source=" + titleOnlyDatabase))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT); INSERT INTO threads (id, title) VALUES ('root-chat', 'Legacy title');";
    command.ExecuteNonQuery();
}
Assert(new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: titleOnlyDatabase).Read(5.5m, chatUsageRoot).Chats!.Single(chat => chat.ThreadId == "root-chat").Title == "Legacy title", "read-only title index supports a legacy title-only threads schema");
var nameAndTitleDatabase = Path.Combine(chatUsageRoot, "name-and-title.sqlite");
using (var connection = new SqliteConnection("Data Source=" + nameAndTitleDatabase))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, name TEXT, title TEXT); INSERT INTO threads (id, name, title) VALUES ('root-chat', 'Preferred name', 'Legacy title');";
    command.ExecuteNonQuery();
}
Assert(new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: nameAndTitleDatabase).Read(5.5m, chatUsageRoot).Chats!.Single(chat => chat.ThreadId == "root-chat").Title == "Preferred name", "read-only title index prefers the current name when both title columns exist");
var whitespaceNameDatabase = Path.Combine(chatUsageRoot, "whitespace-name.sqlite");
using (var connection = new SqliteConnection("Data Source=" + whitespaceNameDatabase))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, name TEXT, title TEXT); INSERT INTO threads (id, name, title) VALUES ('root-chat', '   ', 'Whitespace fallback title');";
    command.ExecuteNonQuery();
}
Assert(new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: whitespaceNameDatabase).Read(5.5m, chatUsageRoot).Chats!.Single(chat => chat.ThreadId == "root-chat").Title == "Whitespace fallback title", "title index accepts a non-empty legacy title when the current name is whitespace");
SqliteConnection.ClearAllPools();
Directory.Delete(chatUsageRoot, true);
File.AppendAllText(Path.Combine(analyticsRoot, "session.jsonl"), "\n");
var cachedAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
_ = cachedAnalytics.Read(5.5m, analyticsRoot);
Assert(cachedAnalytics.FilesParsedLastRead == 1, "initial analytics read parses the source file");
_ = cachedAnalytics.Read(5.5m, analyticsRoot);
Assert(cachedAnalytics.FilesParsedLastRead == 0, "unchanged analytics file is served from cache");
Assert(cachedAnalytics.BytesReadLastRead == 0, "unchanged analytics read consumes zero JSONL bytes");
var appendedLine = "\n{\"timestamp\":\"2026-08-12T10:03:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":200,\"cached_input_tokens\":30,\"output_tokens\":0,\"total_tokens\":200}}}}";
File.AppendAllText(Path.Combine(analyticsRoot, "session.jsonl"), appendedLine);
var appendedUsage = cachedAnalytics.Read(5.5m, analyticsRoot);
Assert(appendedUsage.MonthTokens == 200 && cachedAnalytics.BytesReadLastRead > 0, "append preserves totals after cache validation");
Assert(cachedAnalytics.CachedBucketCount <= 2, "analytics cache retains compact day-model buckets rather than events");
var partialRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-partial-" + Guid.NewGuid());
Directory.CreateDirectory(partialRoot);
var partialPath = Path.Combine(partialRoot, "active.jsonl");
File.WriteAllText(partialPath, "{\"timestamp\":\"2026-08-12T10:00:00Z\",\"payload\":{\"type\":\"turn_context\",\"model\":\"gpt-5.6-terra\"}}\n{\"timestamp\":\"2026-08-12T10:01:00Z\",\"payload\":");
var partialAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
Assert(partialAnalytics.Read(5.5m, partialRoot).MonthTokens == 0, "partial JSONL record does not contribute before newline commit");
File.AppendAllText(partialPath, "{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":10}}}}\n");
Assert(partialAnalytics.Read(5.5m, partialRoot).MonthTokens == 10, "completed partial record is rebuilt and counted exactly once");
Directory.Delete(partialRoot, true);
var rewriteRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-rewrite-" + Guid.NewGuid());
Directory.CreateDirectory(rewriteRoot);
var rewritePath = Path.Combine(rewriteRoot, "rewrite.jsonl");
var rewriteA = "{\"timestamp\":\"2026-08-12T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"total_tokens\":10}}}}\n";
var rewriteB = rewriteA.Replace("10", "20");
File.WriteAllText(rewritePath, rewriteA); var rewriteAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
Assert(rewriteAnalytics.Read(5.5m, rewriteRoot).MonthTokens == 10, "rewrite fixture starts with original total");
File.WriteAllText(rewritePath, rewriteB); File.SetLastWriteTimeUtc(rewritePath, DateTime.UtcNow.AddSeconds(2));
Assert(rewriteAnalytics.Read(5.5m, rewriteRoot).MonthTokens == 20 && rewriteAnalytics.FilesRebuiltLastRead == 1, "same length rewrite invalidates cache");
File.WriteAllText(rewritePath, rewriteA); File.SetLastWriteTimeUtc(rewritePath, DateTime.UtcNow.AddSeconds(3));
Assert(rewriteAnalytics.Read(5.5m, rewriteRoot).MonthTokens == 10 && rewriteAnalytics.FilesRebuiltLastRead == 1, "truncate or rewrite rebuilds correctly");
var shorter = "{\"timestamp\":\"2026-08-12T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":5,\"total_tokens\":5}}}}\n";
File.WriteAllText(rewritePath, shorter); File.SetLastWriteTimeUtc(rewritePath, DateTime.UtcNow.AddSeconds(4));
Assert(rewriteAnalytics.Read(5.5m, rewriteRoot).MonthTokens == 5 && rewriteAnalytics.FilesRebuiltLastRead == 1, "genuine truncation rebuilds correctly");
File.AppendAllText(rewritePath, "{\"timestamp\":\"2026-08-12T10:01:00Z\",\"payload\":{\"type\":\"turn_context\",\"model\":\"gpt-5.6-terra-ação\"}}\n{\"timestamp\":\"2026-08-12T10:02:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":12,\"total_tokens\":12}}}}\n");
var utf8Usage = rewriteAnalytics.Read(5.5m, rewriteRoot);
Assert(utf8Usage.Models.Any(x => x.Model == "gpt-5.6-terra-ação" && x.Tokens == 7), "utf8 append preserves model attribution and token delta");
var grownRewrite = rewriteA + "{\"timestamp\":\"2026-08-12T10:02:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":30,\"total_tokens\":30}}}}\n";
File.WriteAllText(rewritePath, grownRewrite); File.SetLastWriteTimeUtc(rewritePath, DateTime.UtcNow.AddSeconds(5));
Assert(rewriteAnalytics.Read(5.5m, rewriteRoot).MonthTokens == 30 && rewriteAnalytics.FilesRebuiltLastRead == 1, "prefix rewrite with length growth rebuilds rather than appends");
Directory.Delete(rewriteRoot, true);
File.WriteAllText(Path.Combine(analyticsRoot, "switch.jsonl"), """
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"unknown-model"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":20,"cached_input_tokens":0,"output_tokens":0,"total_tokens":20}}}}
{"timestamp":"2026-08-12T10:01:00Z","payload":{"type":"turn_context","model":"gpt-5.6-luna"}}
{"timestamp":"2026-08-12T10:01:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":5,"cached_input_tokens":0,"output_tokens":0,"total_tokens":5}}}}
""");
analytics = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, analyticsRoot);
Assert(analytics.Models.Any(x => x.Model == "unknown-model" && !x.Priced), "unknown model remains visible and unpriced");
var threadSettingsRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-thread-settings-" + Guid.NewGuid());
Directory.CreateDirectory(threadSettingsRoot);
File.WriteAllText(Path.Combine(threadSettingsRoot, "settings.jsonl"), """
{"timestamp":"2026-08-12T10:00:00Z","type":"event_msg","payload":{"type":"model_provider","model":"gpt-5.6-sol"}}
{"timestamp":"2026-08-12T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"total_tokens":10}}}}
{"timestamp":"2026-08-12T10:00:02Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol"}}}
{"timestamp":"2026-08-12T10:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":30,"total_tokens":30}}}}
{"timestamp":"2026-08-12T10:00:04Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-terra"}}}
{"timestamp":"2026-08-12T10:00:05Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":45,"total_tokens":45}}}}
""");
var threadSettingsUsage = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, threadSettingsRoot);
Assert(threadSettingsUsage.Models.Any(x => x.Model == "unknown" && x.Tokens == 10) &&
       threadSettingsUsage.Models.Any(x => x.Model == "gpt-5.6-sol" && x.Tokens == 20) &&
       threadSettingsUsage.Models.Any(x => x.Model == "gpt-5.6-terra" && x.Tokens == 15),
       "thread settings apply model attribution only to subsequent snapshots while model_provider alone remains unknown");
Directory.Delete(threadSettingsRoot, true);
var sqliteFallbackRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-sqlite-fallback-" + Guid.NewGuid());
Directory.CreateDirectory(sqliteFallbackRoot);
var sqliteFallbackPath = Path.Combine(sqliteFallbackRoot, "fallback.jsonl");
File.WriteAllText(sqliteFallbackPath, """
{"timestamp":"2026-08-12T10:00:00Z","type":"session_meta","payload":{"session_id":"sqlite-session","id":"sqlite-thread"}}
{"timestamp":"2026-08-12T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"total_tokens":10}}}}
{"timestamp":"2026-08-12T10:00:02Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-terra"}}}
{"timestamp":"2026-08-12T10:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":25,"total_tokens":25}}}}
""" + Environment.NewLine);
File.WriteAllText(Path.Combine(sqliteFallbackRoot, "unmapped.jsonl"), """
{"timestamp":"2026-08-12T10:00:00Z","type":"session_meta","payload":{"session_id":"unmapped-session","id":"unmapped-thread"}}
{"timestamp":"2026-08-12T10:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":7,"total_tokens":7}}}}
""" + Environment.NewLine);
var sqliteFallbackDb = Path.Combine(sqliteFallbackRoot, "state.sqlite");
using (var connection = new SqliteConnection($"Data Source={sqliteFallbackDb};Pooling=False"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA journal_mode=WAL";
    command.ExecuteScalar();
    command.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, model TEXT, reasoning_effort TEXT); INSERT INTO threads (id, model, reasoning_effort) VALUES ('sqlite-thread', 'gpt-5.6-sol', 'medium');";
    command.ExecuteNonQuery();
}
var sqliteFallbackUsage = new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: sqliteFallbackDb).Read(5.5m, sqliteFallbackRoot);
Assert(sqliteFallbackUsage.Models.Any(x => x.Model == "gpt-5.6-sol" && x.Tokens == 10) && sqliteFallbackUsage.Models.Any(x => x.Model == "gpt-5.6-terra" && x.Tokens == 15) && sqliteFallbackUsage.Models.Any(x => x.Model == "unknown" && x.Tokens == 7), "sqlite thread model seeds attribution before the first snapshot, later rollout settings remain temporal overrides, and missing thread models remain unknown");
var sqliteFallbackService = new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: sqliteFallbackDb);
_ = sqliteFallbackService.Read(5.5m, sqliteFallbackRoot);
var sqliteFallbackWal = sqliteFallbackDb + "-wal";
using (var connection = new SqliteConnection($"Data Source={sqliteFallbackDb};Pooling=False"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE threads SET model = 'gpt-5.6-luna' WHERE id = 'sqlite-thread'";
    command.ExecuteNonQuery();
    Assert(File.Exists(sqliteFallbackWal), "sqlite WAL is present after a journal-mode update");
    File.SetLastWriteTimeUtc(sqliteFallbackWal, DateTime.UtcNow.AddSeconds(1));
    sqliteFallbackUsage = sqliteFallbackService.Read(5.5m, sqliteFallbackRoot);
}
Assert(sqliteFallbackUsage.Models.Any(x => x.Model == "gpt-5.6-luna" && x.Tokens == 10) && sqliteFallbackUsage.Models.Any(x => x.Model == "gpt-5.6-terra" && x.Tokens == 15) && sqliteFallbackService.FilesRebuiltLastRead == 1, "changed sqlite fallback rebuilds only its affected rollout while later temporal settings remain authoritative");
var missingSqliteUsage = new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: Path.Combine(sqliteFallbackRoot, "missing.sqlite")).Read(5.5m, sqliteFallbackRoot);
Assert(missingSqliteUsage.MonthTokens == 32, "missing sqlite fallback leaves local analytics available");
var corruptSqlitePath = Path.Combine(sqliteFallbackRoot, "corrupt.sqlite");
File.WriteAllText(corruptSqlitePath, "not a sqlite database");
var corruptSqliteUsage = new LocalUsageAnalyticsService(() => analyticsNow, stateDatabasePath: corruptSqlitePath).Read(5.5m, sqliteFallbackRoot);
Assert(corruptSqliteUsage.MonthTokens == 32, "corrupt sqlite fallback leaves local analytics available");
using (var lockConnection = new SqliteConnection($"Data Source={sqliteFallbackDb};Pooling=False"))
{
    lockConnection.Open();
    using (var lockCommand = lockConnection.CreateCommand()) { lockCommand.CommandText = "BEGIN EXCLUSIVE"; lockCommand.ExecuteNonQuery(); }
    File.SetLastWriteTimeUtc(sqliteFallbackWal, DateTime.UtcNow.AddSeconds(2));
    var lockedSqliteUsage = sqliteFallbackService.Read(5.5m, sqliteFallbackRoot);
    Assert(lockedSqliteUsage.Models.Any(x => x.Model == "gpt-5.6-luna" && x.Tokens == 10) && sqliteFallbackService.FilesRebuiltLastRead == 0, "locked sqlite fallback preserves the last valid map without rebuilding rollouts as unknown");
    using var rollbackCommand = lockConnection.CreateCommand();
    rollbackCommand.CommandText = "ROLLBACK";
    rollbackCommand.ExecuteNonQuery();
}
Directory.Delete(sqliteFallbackRoot, true);
Directory.Delete(analyticsRoot, true);
var parallelAnalyticsRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-parallel-analytics-" + Guid.NewGuid());
Directory.CreateDirectory(parallelAnalyticsRoot);
for (var index = 0; index < 4; index++)
{
    var total = (index + 1) * 100;
    var contextLine = JsonSerializer.Serialize(new { timestamp = $"2026-08-12T1{index}:00:00Z", payload = new { type = "turn_context", model = $"gpt-5.6-model-{index}" } });
    var tokenLine = JsonSerializer.Serialize(new { timestamp = $"2026-08-12T1{index}:01:00Z", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = total, cached_input_tokens = 0, output_tokens = 0, total_tokens = total } } } });
    File.WriteAllText(Path.Combine(parallelAnalyticsRoot, $"session-{index}.jsonl"), contextLine + "\n" + tokenLine + "\n");
}
var sequentialParallelFixtureService = new LocalUsageAnalyticsService(() => analyticsNow, maxParseParallelism: 1);
var boundedParallelFixtureService = new LocalUsageAnalyticsService(() => analyticsNow, maxParseParallelism: 2);
var sequentialParallelFixture = sequentialParallelFixtureService.Read(5.5m, parallelAnalyticsRoot);
var boundedParallelFixture = boundedParallelFixtureService.Read(5.5m, parallelAnalyticsRoot);
Assert(JsonSerializer.Serialize(boundedParallelFixture) == JsonSerializer.Serialize(sequentialParallelFixture) &&
       sequentialParallelFixtureService.FilesParsedLastRead == 4 && boundedParallelFixtureService.FilesParsedLastRead == 4 &&
       sequentialParallelFixtureService.FilesRebuiltLastRead == 4 && boundedParallelFixtureService.FilesRebuiltLastRead == 4 &&
       sequentialParallelFixtureService.BytesReadLastRead == boundedParallelFixtureService.BytesReadLastRead,
       "four independent cold files produce byte-for-byte identical analytics and counters with sequential and bounded-parallel parsing");
var analyticsServiceSource = File.ReadAllText(FindRepositoryFile("src", "CodexTracker.Core", "LocalUsageAnalyticsService.cs"));
Assert(analyticsServiceSource.Contains("ParseAggregate(plan.File.Path, offset, plan.Signature.Length", StringComparison.Ordinal) &&
       analyticsServiceSource.Contains("new BoundedReadStream(stream, Math.Max(0, snapshotLength - offset))", StringComparison.Ordinal) &&
       analyticsServiceSource.Contains("stream.Seek(length - 1, SeekOrigin.Begin);", StringComparison.Ordinal),
       "cold parsing is bounded to each planned file signature so concurrent writer growth is deferred instead of double-counted");
Directory.Delete(parallelAnalyticsRoot, true);
var forksRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-forks-" + Guid.NewGuid());
Directory.CreateDirectory(forksRoot);
File.WriteAllText(Path.Combine(forksRoot, "root.jsonl"), """
{"type":"session_meta","timestamp":"2026-08-12T10:00:00Z","payload":{"session_id":"s1","id":"s1","thread_source":"root"}}
{"type":"turn_context","timestamp":"2026-08-12T10:00:00Z","payload":{"model":"gpt-5.6-terra"}}
{"type":"event_msg","timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":180,"cached_input_tokens":0,"output_tokens":0,"total_tokens":180}}}}
""");
File.WriteAllText(Path.Combine(forksRoot, "fork.jsonl"), """
{"type":"session_meta","timestamp":"2026-08-12T10:00:00Z","payload":{"session_id":"s1","id":"f1","forked_from_id":"s1","thread_source":"subagent"}}
{"type":"session_meta","timestamp":"2026-08-12T10:00:00Z","payload":{"session_id":"s1","id":"s1","thread_source":"root"}}
{"type":"event_msg","timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":180,"cached_input_tokens":0,"output_tokens":0,"total_tokens":180}}}}
{"type":"turn_context","timestamp":"2026-08-12T10:00:01Z","payload":{"model":"gpt-5.6-sol"}}
{"type":"event_msg","timestamp":"2026-08-12T10:00:02Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":210,"cached_input_tokens":0,"output_tokens":0,"total_tokens":210}}}}
""");
var forked = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, forksRoot);
Assert(forked.MonthTokens == 390 && forked.Models.Any(x => x.Model == "gpt-5.6-terra") && forked.Models.Any(x => x.Model == "gpt-5.6-sol"), "fork counts its inherited context as tokens processed by the child rollout");
File.WriteAllText(Path.Combine(forksRoot, "independent.jsonl"), """
{"type":"session_meta","payload":{"session_id":"s2","id":"s2","thread_source":"root"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-luna"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":40,"cached_input_tokens":0,"output_tokens":0,"total_tokens":40}}}}
""");
var withIndependent = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, forksRoot);
Assert(withIndependent.MonthTokens == 430, "independent root still counts its first snapshot");
File.WriteAllText(Path.Combine(forksRoot, "spark.jsonl"), """
{"type":"session_meta","payload":{"session_id":"s3","id":"s3"}}
{"type":"turn_context","timestamp":"2026-08-12T10:00:00Z","payload":{"model":"gpt-5.3-codex-spark"}}
{"type":"event_msg","timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":0,"total_tokens":10}}}}
""");
var spark = new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, forksRoot);
Assert(spark.Models.Any(x => x.Model == "gpt-5.3-codex-spark" && !x.Priced), "spark ID without official exact price remains unpriced");
Directory.Delete(forksRoot, true);
var restartRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-restart-" + Guid.NewGuid());
Directory.CreateDirectory(restartRoot);
var restartPath = Path.Combine(restartRoot, "restart.jsonl");
File.WriteAllText(restartPath, """
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-terra"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"total_tokens":100}}}}
{"timestamp":"2026-08-12T10:01:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":160,"total_tokens":160}}}}
{"timestamp":"2026-08-12T10:02:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"total_tokens":10}}}}
{"timestamp":"2026-08-12T10:03:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":45,"total_tokens":45}}}}
""");
Assert(new LocalUsageAnalyticsService(() => analyticsNow).Read(5.5m, restartRoot).MonthTokens == 205, "counter reset starts a new cumulative segment and counts its current snapshot");
var appendedForkPath = Path.Combine(restartRoot, "fork-append.jsonl");
File.WriteAllText(appendedForkPath, """{"type":"session_meta","payload":{"session_id":"r1","id":"f1","forked_from_id":"r1","thread_source":"subagent"}}""" + Environment.NewLine);
var appendForkAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
Assert(appendForkAnalytics.Read(5.5m, restartRoot).MonthTokens == 205, "empty fork does not change usage");
File.AppendAllText(appendedForkPath, """{"timestamp":"2026-08-12T10:04:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":45,"total_tokens":45}}}}""" + Environment.NewLine);
Assert(appendForkAnalytics.Read(5.5m, restartRoot).MonthTokens == 250, "first fork snapshot received by append is processed context");
File.AppendAllText(appendedForkPath, """{"timestamp":"2026-08-12T10:05:00Z","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":70,"total_tokens":70}}}}""" + Environment.NewLine);
Assert(appendForkAnalytics.Read(5.5m, restartRoot).MonthTokens == 275, "fork append adds only the subsequent cumulative delta");
Directory.Delete(restartRoot, true);
var duplicateRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-duplicate-rollouts-" + Guid.NewGuid());
Directory.CreateDirectory(duplicateRoot);
var duplicateEarly = Path.Combine(duplicateRoot, "rollout-early.jsonl");
var duplicateFull = Path.Combine(duplicateRoot, "rollout-full.jsonl");
File.WriteAllText(duplicateEarly, """
{"type":"session_meta","payload":{"session_id":"logical-root","id":"logical-root","thread_source":"user"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-terra"}}
{"timestamp":"2026-08-12T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"total_tokens":100}}}}
{"timestamp":"2026-08-12T10:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":150,"total_tokens":150}}}}
""");
File.WriteAllText(duplicateFull, File.ReadAllText(duplicateEarly) + Environment.NewLine + """
{"timestamp":"2026-08-12T10:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":200,"total_tokens":200}}}}
{"timestamp":"2026-08-12T10:03:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":10,"total_tokens":10}}}}
{"timestamp":"2026-08-12T10:04:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":60,"total_tokens":60}}}}
""");
var duplicateAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
var duplicateUsage = duplicateAnalytics.Read(5.5m, duplicateRoot);
Assert(duplicateUsage.MonthTokens == 260, "a prefixed checkpoint counts both cumulative segments while its shorter physical copy is ignored");
Assert(duplicateAnalytics.LogicalStreamsLastRead == 1 && duplicateAnalytics.DuplicatePhysicalFilesIgnoredLastRead == 1, "duplicate rollout diagnostics distinguish physical files from one logical stream");
File.AppendAllText(duplicateEarly, Environment.NewLine + """{"timestamp":"2026-08-12T10:03:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":250,"total_tokens":250}}}}""" + Environment.NewLine);
Assert(duplicateAnalytics.Read(5.5m, duplicateRoot).MonthTokens == 510, "a later divergent segment is retained instead of being discarded solely because its metadata id matches");
Directory.Delete(duplicateRoot, true);
var divergentRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-divergent-rollouts-" + Guid.NewGuid());
Directory.CreateDirectory(divergentRoot);
File.WriteAllText(Path.Combine(divergentRoot, "segment-a.jsonl"), """
{"type":"session_meta","payload":{"session_id":"segment-session","id":"segment-id","thread_source":"root"}}
{"timestamp":"2026-08-12T10:00:00Z","payload":{"type":"turn_context","model":"gpt-5.6-terra"}}
{"timestamp":"2026-08-12T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"total_tokens":100}}}}
""");
File.WriteAllText(Path.Combine(divergentRoot, "segment-b.jsonl"), """
{"type":"session_meta","payload":{"session_id":"segment-session","id":"segment-id","thread_source":"root"}}
{"timestamp":"2026-08-12T10:01:00Z","payload":{"type":"turn_context","model":"gpt-5.6-sol"}}
{"timestamp":"2026-08-12T10:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":70,"total_tokens":70}}}}
""");
var divergentAnalytics = new LocalUsageAnalyticsService(() => analyticsNow);
Assert(divergentAnalytics.Read(5.5m, divergentRoot).MonthTokens == 170, "same metadata id without a physical prefix remains independent evidence rather than being discarded");
Assert(divergentAnalytics.DuplicatePhysicalFilesIgnoredLastRead == 0, "only proven physical checkpoint prefixes are deduplicated");
Directory.Delete(divergentRoot, true);
var activityRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-agent-activity-" + Guid.NewGuid());
Directory.CreateDirectory(activityRoot);
var activityNow = new DateTimeOffset(2026, 8, 14, 13, 30, 0, TimeSpan.Zero);
var activityProjectRoot = Path.Combine(activityRoot, "project-root");
var activityProjectSubdirectory = Path.Combine(activityProjectRoot, "src", "feature");
Directory.CreateDirectory(Path.Combine(activityProjectRoot, ".git"));
Directory.CreateDirectory(activityProjectSubdirectory);
var activityProjectSubdirectoryJson = activityProjectSubdirectory.Replace("\\", "\\\\");
var rootActivityPath = Path.Combine(activityRoot, "root.jsonl");
File.WriteAllText(rootActivityPath, """
{"timestamp":"2026-08-14T13:20:00Z","type":"session_meta","payload":{"session_id":"root-1","id":"root-1","thread_source":"user","cwd":"$PROJECT_CWD$"}}
{"timestamp":"2026-08-14T13:20:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-root"}}
{"timestamp":"2026-08-14T13:20:02Z","type":"turn_context","payload":{"turn_id":"turn-root","model":"gpt-5.6-terra","effort":"medium"}}
{"timestamp":"2026-08-14T13:29:45Z","type":"event_msg","payload":{"type":"agent_reasoning","text":"  **Validando o build e os testes\ncom atencao aos detalhes"}}
{"timestamp":"2026-08-14T13:29:47Z","type":"event_msg","payload":{"type":"agent_reasoning","text":"   "}}
{"timestamp":"2026-08-14T13:29:50Z","type":"event_msg","payload":{"type":"agent_message","phase":"commentary","message":"Validando o build\ncom detalhes"}}
{"timestamp":"2026-08-14T13:29:52Z","type":"response_item","payload":{"type":"message","role":"assistant","phase":"commentary","text":"Output posterior diferente"}}
""".Replace("$PROJECT_CWD$", activityProjectSubdirectoryJson));
var subagentActivityPath = Path.Combine(activityRoot, "subagent.jsonl");
File.WriteAllText(subagentActivityPath, """
{"timestamp":"2026-08-14T13:26:00Z","type":"session_meta","payload":{"session_id":"root-1","id":"sub-1","parent_thread_id":"root-1","thread_source":"subagent","agent_path":"/root/ui_review"}}
{"timestamp":"2026-08-14T13:26:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-sub"}}
{"timestamp":"2026-08-14T13:26:02Z","type":"turn_context","payload":{"turn_id":"turn-sub","model":"gpt-5.6-luna","effort":"low"}}
{"timestamp":"2026-08-14T13:29:55Z","type":"event_msg","payload":{"type":"token_count"}}
""");
var grandchildActivityPath = Path.Combine(activityRoot, "grandchild.jsonl");
File.WriteAllText(grandchildActivityPath, """
{"timestamp":"2026-08-14T13:22:00Z","type":"session_meta","payload":{"session_id":"root-1","id":"grand-1","parent_thread_id":"sub-1","thread_source":"subagent","agent_path":"/root/ui_review/check"}}
{"timestamp":"2026-08-14T13:22:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-grand"}}
{"timestamp":"2026-08-14T13:29:54Z","type":"event_msg","payload":{"type":"token_count"}}
""");
var rootBActivityPath = Path.Combine(activityRoot, "root-b.jsonl");
File.WriteAllText(rootBActivityPath, """
{"timestamp":"2026-08-14T13:23:00Z","type":"session_meta","payload":{"session_id":"root-b","id":"root-b","thread_source":"user"}}
{"timestamp":"2026-08-14T13:23:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-root-b"}}
{"timestamp":"2026-08-14T13:29:53Z","type":"event_msg","payload":{"type":"token_count"}}
""");
var resolvedProjectActivityPath = Path.Combine(activityRoot, "resolved-project.jsonl");
File.WriteAllText(resolvedProjectActivityPath, $"{{\"timestamp\":\"2026-08-14T13:29:30Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"resolved-project\",\"id\":\"resolved-project\",\"cwd\":\"{activityProjectSubdirectoryJson}\"}}}}\n{{\"timestamp\":\"2026-08-14T13:29:31Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"resolved-project-turn\"}}}}\n");
var inheritedProjectActivityPath = Path.Combine(activityRoot, "inherited-project.jsonl");
File.WriteAllText(inheritedProjectActivityPath, """
{"timestamp":"2026-08-14T13:29:32Z","type":"session_meta","payload":{"session_id":"resolved-project","id":"inherited-project","parent_thread_id":"resolved-project","thread_source":"subagent","cwd":"C:\\Users\\luing\\Documents\\Codex\\2026-08-21\\qu"}}
{"timestamp":"2026-08-14T13:29:33Z","type":"event_msg","payload":{"type":"task_started","turn_id":"inherited-project-turn"}}
""");
var transientProjectActivityPath = Path.Combine(activityRoot, "transient-project.jsonl");
File.WriteAllText(transientProjectActivityPath, """
{"timestamp":"2026-08-14T13:29:34Z","type":"session_meta","payload":{"session_id":"transient-project","id":"transient-project","cwd":"C:\\Users\\luing\\Documents\\Codex\\2026-08-21\\qu"}}
{"timestamp":"2026-08-14T13:29:35Z","type":"event_msg","payload":{"type":"task_started","turn_id":"transient-project-turn"}}
""");
var orphanActivityPath = Path.Combine(activityRoot, "orphan.jsonl");
File.WriteAllText(orphanActivityPath, """
{"timestamp":"2026-08-14T13:24:00Z","type":"session_meta","payload":{"session_id":"orphan","id":"orphan","parent_thread_id":"missing-parent","thread_source":"subagent"}}
{"timestamp":"2026-08-14T13:24:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-orphan"}}
{"timestamp":"2026-08-14T13:29:52Z","type":"event_msg","payload":{"type":"token_count"}}
""");
var cycleAActivityPath = Path.Combine(activityRoot, "cycle-a.jsonl");
File.WriteAllText(cycleAActivityPath, """
{"timestamp":"2026-08-14T13:25:00Z","type":"session_meta","payload":{"session_id":"cycle-a","id":"cycle-a","parent_thread_id":"cycle-b","thread_source":"subagent"}}
{"timestamp":"2026-08-14T13:25:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-cycle-a"}}
""");
var cycleBActivityPath = Path.Combine(activityRoot, "cycle-b.jsonl");
File.WriteAllText(cycleBActivityPath, """
{"timestamp":"2026-08-14T13:26:00Z","type":"session_meta","payload":{"session_id":"cycle-b","id":"cycle-b","parent_thread_id":"cycle-a","thread_source":"subagent"}}
{"timestamp":"2026-08-14T13:26:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-cycle-b"}}
""");
File.SetLastWriteTimeUtc(rootActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(subagentActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(grandchildActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(rootBActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(resolvedProjectActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(inheritedProjectActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(transientProjectActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(orphanActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(cycleAActivityPath, activityNow.UtcDateTime);
File.SetLastWriteTimeUtc(cycleBActivityPath, activityNow.UtcDateTime);
var activityService = new AgentActivityService(() => activityNow);
var activeAgents = activityService.Read(new Dictionary<string, string> { ["root-1"] = "Indicador de agentes" }, activityRoot);
Assert(activeAgents.Count == 10, "active task markers expose roots, descendants, orphans and cycles exactly once");
Assert(activeAgents.Select(agent => agent.ThreadId).SequenceEqual(["root-1", "sub-1", "grand-1", "root-b", "orphan", "resolved-project", "inherited-project", "transient-project", "cycle-a", "cycle-b"]), "active agents are depth-first by stable roots and descendants, with cycle fallback order");
var activeRoot = activeAgents.Single(x => x.ThreadId == "root-1");
Assert(activeRoot.Type == "Agent" && activeRoot.HierarchyDepth == 0 && activeRoot.Title == "Indicador de agentes" && activeRoot.Status == "Validando o build e os testes" && activeRoot.Model == "gpt-5.6-terra" && activeRoot.Effort == "medium" && activeRoot.ProjectPath == Path.GetFullPath(activityProjectRoot), "principal activity preserves title, active reasoning, model, effort and canonical session project even after later commentary");
var activeSubagent = activeAgents.Single(x => x.ThreadId == "sub-1");
Assert(activeSubagent.Type == "Subagent" && activeSubagent.HierarchyDepth == 1 && activeSubagent.Title == "ui review" && activeSubagent.ParentThreadId == "root-1" && activeSubagent.Model == "gpt-5.6-luna" && activeSubagent.Effort == "low" && activeSubagent.ProjectPath == Path.GetFullPath(activityProjectRoot), "subagent activity uses its own id, depth, inherited canonical parent project and safe path fallback title");
Assert(activeAgents.Single(agent => agent.ThreadId == "resolved-project").ProjectPath == Path.GetFullPath(activityProjectRoot) && activeAgents.Single(agent => agent.ThreadId == "inherited-project").ProjectPath == Path.GetFullPath(activityProjectRoot) && activeAgents.Single(agent => agent.ThreadId == "transient-project").ProjectPath is null, "agent projects resolve to their canonical Git root, inherit that root through subagents, and label a transient non-project cwd as Sem projeto");
Assert(activeAgents.Single(x => x.ThreadId == "grand-1").HierarchyDepth == 2 && activeAgents.Single(x => x.ThreadId == "root-b").HierarchyDepth == 0 && activeAgents.Single(x => x.ThreadId == "orphan").HierarchyDepth == 0, "grandchildren nest while roots with missing parents remain visual roots");
Assert(activeAgents.Single(x => x.ThreadId == "root-b").Model == "unknown" && activeAgents.Single(x => x.ThreadId == "root-b").Effort == "unknown", "missing turn context initially uses unknown metadata");
File.AppendAllText(rootBActivityPath, "\n{\"timestamp\":\"2026-08-14T13:29:57Z\",\"payload\":{\"type\":\"turn_context\",\"model\":\"gpt-5.6-sol\",\"effort\":\"high\"}}\n");
File.SetLastWriteTimeUtc(rootBActivityPath, activityNow.UtcDateTime.AddSeconds(1));
var appendedContextAgent = activityService.Read(null, activityRoot).Single(x => x.ThreadId == "root-b");
Assert(appendedContextAgent.Model == "gpt-5.6-sol" && appendedContextAgent.Effort == "high", "payload turn_context append replaces unknown metadata through the incremental cache");
var stagnantMtimeActivityPath = Path.Combine(activityRoot, "rollout-2026-08-14T13-29-00-stagnant.jsonl");
File.WriteAllText(stagnantMtimeActivityPath, """
{"timestamp":"2026-08-14T13:29:00Z","type":"session_meta","payload":{"session_id":"stagnant","id":"stagnant","thread_source":"user"}}
{"timestamp":"2026-08-14T13:29:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"stagnant-turn"}}
{"timestamp":"2026-08-14T13:29:02Z","payload":{"type":"turn_context","model":"gpt-5.6-terra","effort":"medium"}}
""");
File.SetLastWriteTimeUtc(stagnantMtimeActivityPath, activityNow.AddMinutes(-10).UtcDateTime);
var stagnantMtimeService = new AgentActivityService(() => activityNow);
var stagnantMtimeAgent = stagnantMtimeService.Read(null, activityRoot).Single(x => x.ThreadId == "stagnant");
Assert(stagnantMtimeAgent.Model == "gpt-5.6-terra" && stagnantMtimeAgent.Effort == "medium", "a current rollout with stale filesystem mtime is discovered from its session date and payload turn context");
File.AppendAllText(stagnantMtimeActivityPath, "\n{\"timestamp\":\"2026-08-14T13:29:59Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_reasoning\",\"text\":\"Evento incremental\"}}\n");
File.SetLastWriteTimeUtc(stagnantMtimeActivityPath, activityNow.AddMinutes(-10).UtcDateTime);
stagnantMtimeAgent = stagnantMtimeService.Read(null, activityRoot).Single(x => x.ThreadId == "stagnant");
Assert(stagnantMtimeAgent.Status == "Evento incremental" && stagnantMtimeAgent.Model == "gpt-5.6-terra" && stagnantMtimeAgent.Effort == "medium", "stale-mtime rollout observes appended bytes incrementally without losing metadata");
var localBoundaryRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-agent-activity-local-boundary-" + Guid.NewGuid());
Directory.CreateDirectory(localBoundaryRoot);
var localBoundaryWallClock = new DateTime(2026, 8, 14, 23, 58, 0, DateTimeKind.Unspecified);
var localBoundaryNow = new DateTimeOffset(localBoundaryWallClock, TimeZoneInfo.Local.GetUtcOffset(localBoundaryWallClock));
var localBoundaryPath = Path.Combine(localBoundaryRoot, $"rollout-{localBoundaryNow.LocalDateTime:yyyy-MM-dd}T23-58-00-local.jsonl");
File.WriteAllText(localBoundaryPath, $"{{\"timestamp\":\"{localBoundaryNow.UtcDateTime:O}\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"local-boundary\",\"id\":\"local-boundary\"}}}}\n{{\"timestamp\":\"{localBoundaryNow.AddSeconds(1).UtcDateTime:O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"local-boundary-turn\"}}}}\n");
File.SetLastWriteTimeUtc(localBoundaryPath, localBoundaryNow.AddDays(-2).UtcDateTime);
Assert(new AgentActivityService(() => localBoundaryNow).Read(null, localBoundaryRoot).Single().ThreadId == "local-boundary", "rollout filename bootstrap uses the local calendar at the UTC date boundary");
Directory.Delete(localBoundaryRoot, true);
var evictionRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-agent-activity-eviction-" + Guid.NewGuid());
Directory.CreateDirectory(evictionRoot);
var evictionPath = Path.Combine(evictionRoot, "old-rollout.jsonl");
File.WriteAllText(evictionPath, """
{"timestamp":"2026-08-14T13:29:00Z","type":"session_meta","payload":{"session_id":"evict","id":"evict","thread_source":"user"}}
{"timestamp":"2026-08-14T13:29:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"evict-turn"}}
""");
File.SetLastWriteTimeUtc(evictionPath, activityNow.UtcDateTime);
var evictionNow = activityNow;
var evictionService = new AgentActivityService(() => evictionNow);
Assert(evictionService.Read(null, evictionRoot).Single().ThreadId == "evict" && CachedRolloutCount(evictionService) == 1, "recent mtime rollout enters the activity cache");
evictionNow = activityNow.AddDays(2);
Assert(evictionService.Read(null, evictionRoot).Count == 0 && CachedRolloutCount(evictionService) == 0, "unchanged stale rollout is evicted instead of remaining cached indefinitely");
Directory.Delete(evictionRoot, true);
File.AppendAllText(rootActivityPath, "\n{\"timestamp\":\"2026-08-14T13:29:58Z\",\"type\":\"event_msg\",\"payload\":");
File.SetLastWriteTimeUtc(rootActivityPath, activityNow.UtcDateTime.AddSeconds(1));
Assert(activityService.Read(null, activityRoot).Any(x => x.ThreadId == "root-1"), "partial active JSONL line does not erase the committed running state");
File.AppendAllText(rootActivityPath, "{\"type\":\"task_complete\",\"turn_id\":\"turn-root\"}}\n");
File.SetLastWriteTimeUtc(rootActivityPath, activityNow.UtcDateTime.AddSeconds(2));
Assert(activityService.Read(null, activityRoot).All(x => x.ThreadId != "root-1"), "matching task_complete removes the principal from the running list after a partial append completes");
var completedSnapshot = activityService.ReadSnapshot(new Dictionary<string, string> { ["root-1"] = "Indicador de agentes" }, activityRoot);
var completedRoot = completedSnapshot.CompletedAgentWorks.Single(work => work.ThreadId == "root-1");
Assert(completedRoot.CompletionId == "root-1:turn-root" && completedRoot.Title == "Indicador de agentes" && completedRoot.Status == "Concluído" && completedRoot.CompletedAt == new DateTimeOffset(2026, 8, 14, 13, 29, 58, TimeSpan.Zero) && completedRoot.ProjectPath == Path.GetFullPath(activityProjectRoot), "principal task completion remains addressable with its title, canonical project and exact turn identity for unread tracking");
File.AppendAllText(subagentActivityPath, "\n{\"timestamp\":\"2026-08-14T13:29:59Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-sub\"}}\n");
File.SetLastWriteTimeUtc(subagentActivityPath, activityNow.UtcDateTime.AddSeconds(3));
Assert(activityService.ReadSnapshot(null, activityRoot).CompletedAgentWorks.All(work => work.ThreadId != "sub-1"), "subagent completions never enter the unread principal-work list");
var memoryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "memories");
var memoryActivityPath = Path.Combine(activityRoot, "memory-root.jsonl");
var memoryCompletedPath = Path.Combine(activityRoot, "memory-descendant-completed.jsonl");
var memoryPrefixPath = Path.Combine(activityRoot, "memory-prefix.jsonl");
var normalActivityPath = Path.Combine(activityRoot, "normal-project.jsonl");
var invalidPath = Path.Combine(activityRoot, "invalid-cwd.jsonl");
var memoryRootJson = memoryRoot.Replace("\\", "\\\\");
var memoryDescendantJson = Path.Combine(memoryRoot, "rollout_summaries").Replace("\\", "\\\\");
var memoryPrefixJson = (memoryRoot + "-sibling").Replace("\\", "\\\\");
File.WriteAllText(memoryActivityPath, $"{{\"timestamp\":\"2026-08-14T13:29:00Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"memory-root\",\"id\":\"memory-root\",\"cwd\":\"{memoryRootJson}\"}}}}\n{{\"timestamp\":\"2026-08-14T13:29:01Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"memory-root-turn\"}}}}\n");
File.WriteAllText(memoryCompletedPath, $"{{\"timestamp\":\"2026-08-14T13:29:00Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"memory-completed\",\"id\":\"memory-completed\",\"cwd\":\"{memoryDescendantJson}\"}}}}\n{{\"timestamp\":\"2026-08-14T13:29:01Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"memory-completed-turn\"}}}}\n{{\"timestamp\":\"2026-08-14T13:29:02Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_complete\",\"turn_id\":\"memory-completed-turn\"}}}}\n");
File.WriteAllText(memoryPrefixPath, $"{{\"timestamp\":\"2026-08-14T13:29:00Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"memory-prefix\",\"id\":\"memory-prefix\",\"cwd\":\"{memoryPrefixJson}\"}}}}\n{{\"timestamp\":\"2026-08-14T13:29:01Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"memory-prefix-turn\"}}}}\n");
File.WriteAllText(normalActivityPath, """
{"timestamp":"2026-08-14T13:29:00Z","type":"session_meta","payload":{"session_id":"normal-project","id":"normal-project","cwd":"D:\\Dev\\codex-tracker"}}
{"timestamp":"2026-08-14T13:29:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"normal-project-turn"}}
""");
File.WriteAllText(invalidPath, """
{"timestamp":"2026-08-14T13:29:00Z","type":"session_meta","payload":{"session_id":"invalid-cwd","id":"invalid-cwd","cwd":"\u0000"}}
{"timestamp":"2026-08-14T13:29:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"invalid-cwd-turn"}}
""");
foreach (var path in new[] { memoryActivityPath, memoryCompletedPath, memoryPrefixPath, normalActivityPath, invalidPath }) File.SetLastWriteTimeUtc(path, activityNow.UtcDateTime.AddSeconds(4));
var memoryFilteredSnapshot = activityService.ReadSnapshot(null, activityRoot);
Assert(!memoryFilteredSnapshot.ActiveAgents.Any(agent => agent.ThreadId == "memory-root") && !memoryFilteredSnapshot.CompletedAgentWorks.Any(work => work.ThreadId == "memory-completed"), "memory-root active work and memory-descendant completed work are excluded from the agent lists");
Assert(memoryFilteredSnapshot.ActiveAgents.Any(agent => agent.ThreadId == "memory-prefix") && memoryFilteredSnapshot.ActiveAgents.Any(agent => agent.ThreadId == "normal-project") && memoryFilteredSnapshot.ActiveAgents.Any(agent => agent.ThreadId == "invalid-cwd"), "a sibling sharing the memories prefix, a normal project session, and an invalid cwd remain visible");
var staleActivityPath = Path.Combine(activityRoot, "stale.jsonl");
File.WriteAllText(staleActivityPath, """
{"timestamp":"2026-08-14T12:00:00Z","type":"session_meta","payload":{"session_id":"stale","id":"stale","thread_source":"user"}}
{"timestamp":"2026-08-14T12:00:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"stale-turn"}}
""");
File.SetLastWriteTimeUtc(staleActivityPath, activityNow.AddMinutes(-10).UtcDateTime);
Assert(activityService.Read(null, activityRoot).All(x => x.ThreadId != "stale"), "abandoned task_started files age out instead of remaining falsely active");
Directory.Delete(activityRoot, true);

Assert(SemanticVersion.TryParse("1.2.3", out var semverBase) && semverBase == new SemanticVersion(1, 2, 3, null), "semantic version parses a bare major.minor.patch");
Assert(SemanticVersion.TryParse("v1.2.3", out var semverVPrefixed) && semverVPrefixed == semverBase, "semantic version strips a leading v prefix");
Assert(!SemanticVersion.TryParse("1.2", out _) && !SemanticVersion.TryParse("1.2.x", out _) && !SemanticVersion.TryParse("", out _) && !SemanticVersion.TryParse(null, out _), "semantic version rejects malformed or missing input");
Assert(SemanticVersion.TryParse("1.2.4", out var semverNewerPatch) && semverNewerPatch.IsNewerThan(semverBase), "a higher patch is newer");
Assert(SemanticVersion.TryParse("1.3.0", out var semverNewerMinor) && semverNewerMinor.IsNewerThan(semverBase) && !semverBase.IsNewerThan(semverNewerMinor), "a higher minor outranks a higher patch of the previous minor");
Assert(SemanticVersion.TryParse("1.2.3-beta.1", out var semverPrerelease) && semverBase.IsNewerThan(semverPrerelease) && !semverPrerelease.IsNewerThan(semverBase), "a release outranks its own prerelease");
Assert(SemanticVersion.TryParse("1.2.3-alpha", out var semverAlpha) && SemanticVersion.TryParse("1.2.3-beta", out var semverBeta) && semverBeta.IsNewerThan(semverAlpha), "prerelease identifiers compare lexically");
Assert(SemanticVersion.TryParse("1.2.3-2", out var semverPrereleaseTwo) && SemanticVersion.TryParse("1.2.3-10", out var semverPrereleaseTen) && semverPrereleaseTen.IsNewerThan(semverPrereleaseTwo), "numeric prerelease identifiers compare numerically rather than lexically");

const string releaseJson = """
{"tag_name":"v0.14.0","draft":false,"prerelease":false,"assets":[{"name":"CodexTracker-Setup-0.14.0.exe","browser_download_url":"https://example.com/CodexTracker-Setup-0.14.0.exe"},{"name":"CodexTracker-Setup-0.13.3.exe","browser_download_url":"https://example.com/CodexTracker-Setup-0.13.3.exe"}]}
""";
var parsedRelease = GithubReleaseParser.Parse(releaseJson)!;
Assert(parsedRelease is { TagName: "v0.14.0", Prerelease: false, Draft: false } && parsedRelease.Assets.Count == 2, "github release parsing captures the tag, flags and asset list");
Assert(UpdateEvaluator.SelectInstallerAsset(parsedRelease.Assets, new SemanticVersion(0, 14, 0, null))?.DownloadUrl == "https://example.com/CodexTracker-Setup-0.14.0.exe", "installer asset selection matches the versioned setup artifact for the published release");
var newerAvailability = UpdateEvaluator.Evaluate("0.13.3", parsedRelease);
Assert(newerAvailability is { IsAvailable: true, LatestVersion: "0.14.0", DownloadUrl: "https://example.com/CodexTracker-Setup-0.14.0.exe" }, "a higher published release is offered as an available update with its matching versioned installer URL");
Assert(!UpdateEvaluator.Evaluate("0.14.0", parsedRelease).IsAvailable && !UpdateEvaluator.Evaluate("0.15.0", parsedRelease).IsAvailable, "the same or a newer local version reports no available update");
Assert(!UpdateEvaluator.Evaluate("not-a-semver", parsedRelease).IsAvailable, "an invalid local version fails closed instead of treating it as 0.0.0");
const string prereleaseJson = """{"tag_name":"v0.15.0-beta.1","draft":false,"prerelease":true,"assets":[{"name":"CodexTracker-latest.exe","browser_download_url":"https://example.com/beta.exe"}]}""";
Assert(!UpdateEvaluator.Evaluate("0.13.3", GithubReleaseParser.Parse(prereleaseJson)).IsAvailable, "a prerelease release is never offered as an update");
const string wrongVersionAssetJson = """{"tag_name":"v0.14.0","draft":false,"prerelease":false,"assets":[{"name":"CodexTracker-Setup-0.13.3.exe","browser_download_url":"https://example.com/setup.exe"}]}""";
Assert(!UpdateEvaluator.Evaluate("0.13.3", GithubReleaseParser.Parse(wrongVersionAssetJson)).IsAvailable, "a setup artifact whose version differs from the release tag is never offered as an update");
const string legacyAssetJson = """{"tag_name":"v0.14.0","draft":false,"prerelease":false,"assets":[{"name":"CodexTracker-latest.exe","browser_download_url":"https://example.com/legacy.exe"}]}""";
Assert(UpdateEvaluator.Evaluate("0.13.3", GithubReleaseParser.Parse(legacyAssetJson)) is { IsAvailable: true, DownloadUrl: "https://example.com/legacy.exe" }, "the exact legacy installer name remains a safe compatibility fallback");
Assert(UpdateEvaluator.Evaluate("0.13.3", null) is { IsAvailable: false, LatestVersion: null }, "a failed release lookup reports no available update");

var updateCheckNow = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
Assert(UpdateCheckPolicy.IsDue(null, updateCheckNow), "an update check with no recorded history is due immediately");
Assert(!UpdateCheckPolicy.IsDue(updateCheckNow.AddHours(-23), updateCheckNow), "an update check inside the daily window is not due");
Assert(UpdateCheckPolicy.IsDue(updateCheckNow.AddHours(-24), updateCheckNow), "an update check exactly at the daily boundary is due");

var sameAutomaticCheckCycle = updateCheckNow.AddHours(-4);
Assert(UpdateDeferralPolicy.ShouldSuppressAutomaticPrompt("0.14.0", updateCheckNow.AddHours(-1), "0.14.0", sameAutomaticCheckCycle), "deferring a version suppresses its automatic prompt for the current daily check cycle");
Assert(!UpdateDeferralPolicy.ShouldSuppressAutomaticPrompt("0.14.0", updateCheckNow.AddHours(-1), "0.14.0", updateCheckNow), "a deferred prompt is eligible again at the next daily check even when less than 24 hours have elapsed since the click");
Assert(!UpdateDeferralPolicy.ShouldSuppressAutomaticPrompt("0.14.0", updateCheckNow.AddHours(-1), "0.15.0", sameAutomaticCheckCycle), "deferring one version never suppresses a newer version's prompt");
Assert(!UpdateDeferralPolicy.ShouldSuppressAutomaticPrompt(null, null, "0.14.0", sameAutomaticCheckCycle), "no prior deferral never suppresses the prompt");

var updateSettingsTestDirectory = Path.Combine(Path.GetTempPath(), "CodexTracker.Tests", Guid.NewGuid().ToString("N"));
var updateSettingsTestPath = Path.Combine(updateSettingsTestDirectory, "settings.json");
try
{
    var updateDeferredAt = new DateTimeOffset(2026, 8, 17, 9, 30, 0, TimeSpan.Zero);
    var lastChecked = new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
    var updateSettings = new AppSettings(LastUpdateCheckUtc: lastChecked, DeferredUpdateVersion: "0.14.0", UpdateDeferredAtUtc: updateDeferredAt);
    new SettingsStore(updateSettingsTestPath).Save(updateSettings);
    var reloadedUpdateSettings = new SettingsStore(updateSettingsTestPath).Load();
    Assert(reloadedUpdateSettings.LastUpdateCheckUtc == lastChecked && reloadedUpdateSettings.DeferredUpdateVersion == "0.14.0" && reloadedUpdateSettings.UpdateDeferredAtUtc == updateDeferredAt, "update check and deferral state round trip through settings persistence");
}
finally
{
    if (Directory.Exists(updateSettingsTestDirectory)) Directory.Delete(updateSettingsTestDirectory, true);
}
Assert(SettingsStore.Normalize(new AppSettings(DeferredUpdateVersion: "   ")).DeferredUpdateVersion is null, "a blank deferred update version normalizes to null so it never suppresses future prompts");

Assert(mainWindowSource.Contains("_ = CheckForUpdatesIfDueAsync();", StringComparison.Ordinal), "startup schedules an update check alongside quota, agents and local analytics");
var refreshMethodStart = mainWindowSource.IndexOf("private async Task RefreshAsync()", StringComparison.Ordinal);
var refreshMethodEnd = mainWindowSource.IndexOf("private async Task RefreshAnalyticsAsync()", refreshMethodStart, StringComparison.Ordinal);
var refreshMethod = refreshMethodStart >= 0 && refreshMethodEnd > refreshMethodStart ? mainWindowSource.Substring(refreshMethodStart, refreshMethodEnd - refreshMethodStart) : string.Empty;
Assert(refreshMethod.Contains("_ = CheckForUpdatesIfDueAsync();", StringComparison.Ordinal) && refreshMethod.IndexOf("_ = CheckForUpdatesIfDueAsync();", StringComparison.Ordinal) < refreshMethod.IndexOf("if (_client is null)", StringComparison.Ordinal), "the 60-second refresh timer keeps daily update checks alive even while the app-server is disconnected");
Assert(mainWindowSource.Contains("Persist the automatic attempt before touching the network", StringComparison.Ordinal) && mainWindowSource.Contains("_pendingUpdate = null;", StringComparison.Ordinal), "a failed automatic check is rate-limited for a day and cannot show a stale pending update");
Assert(mainWindowSource.Contains("else if (_viewModel.Expanded) MaybeShowPendingUpdateDialog();", StringComparison.Ordinal), "an update found while compact stays pending and only asks to show once detailed mode is confirmed active");
var mainWindowSourceToggleDetailedStart = mainWindowSource.IndexOf("private void ToggleDetailed(", StringComparison.Ordinal);
var mainWindowSourceToggleDetailedEnd = mainWindowSource.IndexOf("private void Settings(", mainWindowSourceToggleDetailedStart, StringComparison.Ordinal);
var toggleDetailedBody = mainWindowSource.Substring(mainWindowSourceToggleDetailedStart, mainWindowSourceToggleDetailedEnd - mainWindowSourceToggleDetailedStart);
var toggleDetailedRefreshAnalyticsIndex = toggleDetailedBody.LastIndexOf("_ = RefreshAnalyticsAsync();", StringComparison.Ordinal);
var toggleDetailedMaybeShowIndex = toggleDetailedBody.IndexOf("MaybeShowPendingUpdateDialog();", StringComparison.Ordinal);
var toggleDetailedExpandedGuardIndex = toggleDetailedBody.LastIndexOf("if (_viewModel.Expanded)", StringComparison.Ordinal);
Assert(toggleDetailedRefreshAnalyticsIndex >= 0 && toggleDetailedMaybeShowIndex > toggleDetailedRefreshAnalyticsIndex &&
       toggleDetailedMaybeShowIndex - toggleDetailedRefreshAnalyticsIndex < 80 && toggleDetailedExpandedGuardIndex < toggleDetailedMaybeShowIndex,
       "switching into detailed mode reveals a pending update dialog right after the existing expanded-only analytics refresh, never in compact mode");
Assert(mainWindowSource.Contains("private void DeferUpdate(object sender, RoutedEventArgs e)", StringComparison.Ordinal) &&
       mainWindowSource.Contains("DeferredUpdateVersion = version, UpdateDeferredAtUtc = DateTimeOffset.UtcNow", StringComparison.Ordinal),
       "deferring an update persists the version and timestamp used to suppress only its current automatic check cycle");
Assert(mainWindowXaml.Contains("Click=\"CheckForUpdatesManual\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Loc.CheckForUpdates", StringComparison.Ordinal), "settings expose a manual check-for-updates action");
Assert(mainWindowXaml.Contains("x:Name=\"UpdateDialogPanel\"", StringComparison.Ordinal) &&
       mainWindowXaml.Contains("Visibility=\"{Binding IsUpdateDialogOpen, Converter={StaticResource BooleanToVisibility}}\"", StringComparison.Ordinal) &&
       mainWindowXaml.Contains("Click=\"StartUpdate\"", StringComparison.Ordinal) && mainWindowXaml.Contains("Click=\"DeferUpdate\"", StringComparison.Ordinal),
       "the update dialog is themed like the rest of the widget and exposes update/defer actions");
Assert(LocalizationManager.HasTextKey("CheckForUpdates") && LocalizationManager.HasTextKey("UpdateAvailableTitle") && LocalizationManager.HasTextKey("UpdateNow") && LocalizationManager.HasTextKey("UpdateLater") && LocalizationManager.HasTextKey("UpdateFailed"), "update strings are localized in both supported languages");

// Claude profile: deterministic engagement and permitted-window selection matrix.
var claudeTestNow = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
IReadOnlyList<QuotaWindow> profileCodexWindows = [new("codex:primary", "5h", 20, claudeTestNow.AddHours(2), 300), new("codex:secondary", "7d", 80, claudeTestNow.AddDays(2), 10080)];
IReadOnlyList<QuotaWindow> profileClaudeWindows = [new("claude:five_hour", "5h", 70, claudeTestNow.AddHours(1), 300), new("claude:seven_day", "7d", 40, claudeTestNow.AddDays(3), 10080)];
var profileStates = Enumerable.Range(0, 16).Select(bits => new ProfileActivity((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0)).ToArray();
foreach (var codexProfile in profileStates)
foreach (var claudeProfile in profileStates)
foreach (var widgetActive in new[] { false, true })
{
    var codexEngaged = codexProfile.HasActiveWork || codexProfile.HasUnreadWork || codexProfile.IsForeground && !codexProfile.IsMinimized;
    var claudeEngaged = claudeProfile.HasActiveWork || claudeProfile.HasUnreadWork || claudeProfile.IsForeground && !claudeProfile.IsMinimized;
    Assert(WidgetVisibilityPolicy.ShouldShow(codexProfile, claudeProfile, widgetActive) == (codexEngaged || claudeEngaged || widgetActive), "profile visibility matrix: focus, minimized, work, unread and widget activity");
    foreach (var display in new[] { "5h", "7d", "both" })
    {
        var selected = ProfileEngagementPolicy.Select(codexProfile, claudeProfile, AgentProvider.Codex, display, profileCodexWindows, profileClaudeWindows);
        Assert(selected.Count == (display == "both" || codexEngaged && claudeEngaged ? 2 : 1), "profile gauge count follows engagement and compact preference");
        if (codexEngaged && claudeEngaged)
        {
            Assert(selected.Select(item => item.Provider).SequenceEqual([AgentProvider.Codex, AgentProvider.Claude]), "dual profile order is always Codex then Claude");
            Assert(selected[0].Window == (display == "5h" ? "5h" : "7d") && selected[1].Window == (display == "7d" ? "7d" : "5h"), "dual gauges choose the most restrictive permitted window independently");
        }
        else Assert(selected.All(item => item.Provider == (claudeEngaged ? AgentProvider.Claude : AgentProvider.Codex)), "single engagement and initial widget-only fallback choose the correct provider");
    }
}
Assert(ProfileEngagementPolicy.Select(default, default, AgentProvider.Claude, "both", profileCodexWindows, profileClaudeWindows).All(item => item.Provider == AgentProvider.Claude), "widget-only display remembers Claude");
var claudeProfileViewModel = new MainViewModel(clock: () => claudeTestNow);
claudeProfileViewModel.ApplyQuota(new(profileCodexWindows, null, null, null, claudeTestNow));
claudeProfileViewModel.ApplyClaude(new(profileClaudeWindows, null, null, null, claudeTestNow), ClaudeConnectionState.Connected, false);
claudeProfileViewModel.SetProfileActivity(new(true, false, false, false), new(false, false, true, false));
Assert(claudeProfileViewModel.CompactQuotaCount == 2 && claudeProfileViewModel.CompactLeftLabel == "7d" && claudeProfileViewModel.CompactRightLabel == "5h" && claudeProfileViewModel.CompactLeftPercent == "20%" && claudeProfileViewModel.CompactRightPercent == "30%", "view model binds independent restrictive percentages and labels");
claudeProfileViewModel.SetProfileActivity(default, new(false, false, false, true));
claudeProfileViewModel.SetProfileActivity(default, default);
Assert(claudeProfileViewModel.CompactLeftPercent == "30%" && claudeProfileViewModel.CompactRightPercent == "60%", "last displayed profile survives widget focus");
claudeProfileViewModel.SetClaudeEnabled(false);
Assert(claudeProfileViewModel.CompactLeftPercent == "80%" && claudeProfileViewModel.CompactRightPercent == "20%", "disabling Claude resets widget fallback to Codex");

// Registrations, desktop metadata cache, defensive sharing and unread transitions.
var claudeFixtureRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-claude-" + Guid.NewGuid().ToString("N"));
var claudeSessionsRoot = Path.Combine(claudeFixtureRoot, "sessions");
var claudeDesktopRoot = Path.Combine(claudeFixtureRoot, "desktop", "nested");
Directory.CreateDirectory(claudeSessionsRoot);
Directory.CreateDirectory(claudeDesktopRoot);
try
{
    var livePids = new HashSet<int> { 101, 102, 103, 104, 105, 106, 107 };
    WriteClaudeSession(claudeSessionsRoot, 101, "named", "busy", "interactive", "Named session", @"D:\Dev\fallback", claudeTestNow);
    WriteClaudeSession(claudeSessionsRoot, 102, "desktop", "busy", "interactive", null, @"D:\Dev\fallback", claudeTestNow);
    WriteClaudeSession(claudeSessionsRoot, 103, "cwd", "idle", "interactive", null, @"D:\Dev\cwd-fallback\", claudeTestNow);
    WriteClaudeSession(claudeSessionsRoot, 104, "fallback", "unrecognized", "interactive", null, null, claudeTestNow);
    WriteClaudeSession(claudeSessionsRoot, 105, "headless", "busy", "headless", null, null, claudeTestNow);
    WriteClaudeSession(claudeSessionsRoot, 999, "dead", "busy", "interactive", null, null, claudeTestNow);
    File.WriteAllText(Path.Combine(claudeSessionsRoot, "106.json"), "{partial");
    var metadataPath = Path.Combine(claudeDesktopRoot, "local_desktop.json");
    File.WriteAllText(Path.Combine(claudeDesktopRoot, "local_named.json"), JsonSerializer.Serialize(new { cliSessionId = "named", title = "Desktop title loses to registration name" }));
    File.WriteAllText(metadataPath, JsonSerializer.Serialize(new { cliSessionId = "desktop", title = "Desktop title", model = "claude-sonnet", effort = "high", lastFocusedAt = claudeTestNow.AddMinutes(-5).ToUnixTimeMilliseconds() }));
    var registrationService = new ClaudeSessionActivityService(claudeSessionsRoot, Path.GetDirectoryName(claudeDesktopRoot), livePids.Contains, () => claudeTestNow);
    var registrations = registrationService.ReadSessions();
    Assert(registrations.Count == 4 && registrations.Single(item => item.SessionId == "named").Title == "Named session" && registrations.Single(item => item.SessionId == "desktop").Title == "Desktop title" && registrations.Single(item => item.SessionId == "cwd").Title == "cwd-fallback" && registrations.Single(item => item.SessionId == "fallback").Title == "Claude", "Claude registration filters and full title fallback chain");
    var metadataSession = registrations.Single(item => item.SessionId == "desktop");
    Assert(metadataSession.Model == "claude-sonnet" && metadataSession.Effort == "high" && metadataSession.LastFocusedAt == claudeTestNow.AddMinutes(-5), "desktop metadata joins only by cliSessionId");
    registrationService.ReadSessions();
    Assert(registrationService.MetadataFilesParsedLastRead == 0, "unchanged large desktop metadata is cached by path and mtime");
    using (var writer = new FileStream(Path.Combine(claudeSessionsRoot, "101.json"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        Assert(registrationService.ReadSessions().Any(item => item.SessionId == "named"), "Claude registration stays readable while writer holds it open");
    var firstClaudeActivity = registrationService.ReadSnapshot();
    Assert(firstClaudeActivity.ActiveAgents.Count == 2 && firstClaudeActivity.CompletedAgentWorks.Count == 0 && firstClaudeActivity.ActiveAgents.All(item => item.Provider == AgentProvider.Claude), "only busy Claude sessions are active; startup idle/unknown never creates completion");
    WriteClaudeSession(claudeSessionsRoot, 101, "named", "idle", "interactive", "Named session", null, claudeTestNow.AddMinutes(1));
    var claudeCompletion = registrationService.ReadSnapshot().CompletedAgentWorks.Single();
    Assert(claudeCompletion.ThreadId == "named" && claudeCompletion.Provider == AgentProvider.Claude && claudeCompletion.CompletedAt == claudeTestNow.AddMinutes(1), "observed busy to idle creates a provider-specific completion");
    Assert(registrationService.ReadSnapshot().CompletedAgentWorks.Count == 0, "idle registration does not repeat the same completion");
    Assert(!ClaudeSessionActivityService.ShouldRemoveCompletion(claudeCompletion, [], new Dictionary<string, DateTimeOffset> { ["named"] = claudeCompletion.CompletedAt.AddMilliseconds(-1), ["other"] = claudeCompletion.CompletedAt.AddHours(1) }), "earlier focus and another session's focus keep unread work");
    Assert(!ClaudeSessionActivityService.ShouldRemoveCompletion(claudeCompletion, [], new Dictionary<string, DateTimeOffset> { ["named"] = claudeCompletion.CompletedAt }), "equal focus timestamp keeps unread work");
    Assert(ClaudeSessionActivityService.ShouldRemoveCompletion(claudeCompletion, [], new Dictionary<string, DateTimeOffset> { ["named"] = claudeCompletion.CompletedAt.AddMilliseconds(1) }), "later focus on the same session confirms read");
    WriteClaudeSession(claudeSessionsRoot, 101, "named", "busy", "interactive", "Named session", null, claudeTestNow.AddMinutes(2));
    var restartedClaude = registrationService.ReadSnapshot();
    Assert(ClaudeSessionActivityService.ShouldRemoveCompletion(claudeCompletion, restartedClaude.ActiveAgents, new Dictionary<string, DateTimeOffset>()) && restartedClaude.ActiveAgents.Single(item => item.ThreadId == "named").StartedAt == claudeTestNow.AddMinutes(2), "returning to busy removes unread and resets elapsed");
    livePids.Remove(101);
    Assert(registrationService.ReadSnapshot().CompletedAgentWorks.Count == 0, "process disappearance while busy never creates completion");
    livePids.Add(101);
    WriteClaudeSession(claudeSessionsRoot, 101, "named", "idle", "interactive", "Named session", null, claudeTestNow.AddMinutes(3));
    Assert(registrationService.ReadSnapshot().CompletedAgentWorks.Count == 0, "later idle after a missing process is not an observed busy transition");
    WriteClaudeSession(claudeSessionsRoot, 102, "desktop", "unknown", "interactive", null, null, claudeTestNow.AddMinutes(4));
    Assert(registrationService.ReadSnapshot().CompletedAgentWorks.Count == 1, "busy to any non-busy status ends observed work fail-closed");
    File.WriteAllText(metadataPath, JsonSerializer.Serialize(new { cliSessionId = "desktop", title = "New desktop title", model = "claude-opus", effort = "low", lastFocusedAt = claudeTestNow.AddMinutes(6).ToUnixTimeMilliseconds() }));
    File.SetLastWriteTimeUtc(metadataPath, claudeTestNow.AddMinutes(6).UtcDateTime);
    Assert(registrationService.ReadSessions().Single(item => item.SessionId == "desktop").Title == "New desktop title" && registrationService.MetadataFilesParsedLastRead == 1, "desktop mtime change invalidates only the affected metadata cache");
    var gitProject = Path.Combine(claudeFixtureRoot, "project");
    Directory.CreateDirectory(Path.Combine(gitProject, ".git"));
    Directory.CreateDirectory(Path.Combine(gitProject, "child"));
    WriteClaudeSession(claudeSessionsRoot, 107, "project", "busy", "interactive", null, Path.Combine(gitProject, "child"), claudeTestNow);
    Assert(registrationService.ReadSnapshot().ActiveAgents.Single(item => item.ThreadId == "project").ProjectPath == gitProject, "Claude project uses the shared Git root resolver");
    File.Delete(metadataPath);
    Assert(!registrationService.ReadSessions().Any(item => item.LastFocusedAt.HasValue), "deleted desktop metadata does not retain stale focus identity");

    var legacyCompleted = JsonSerializer.Deserialize<CompletedAgentWork>("""{"CompletionId":"legacy:done","ThreadId":"legacy","Type":"Agent","Title":"Legacy","Status":"Completed","Model":"unknown","Effort":"unknown","StartedAt":"2026-10-01T10:00:00Z","CompletedAt":"2026-10-01T11:00:00Z"}""")!;
    Assert(legacyCompleted.Provider == AgentProvider.Codex, "persisted work without Provider deserializes as Codex");
    var oldSettingsPath = Path.Combine(claudeFixtureRoot, "settings.json");
    File.WriteAllText(oldSettingsPath, "{}");
    var legacyClaudeSettings = new SettingsStore(oldSettingsPath).Load();
    Assert(legacyClaudeSettings.ClaudeProfileEnabled && legacyClaudeSettings.ClaudeAccentColor == "#D97757", "old settings receive compatible enabled/default Claude color");
    var sameIdCodex = legacyCompleted with { ThreadId = "named" };
    var combinedSettings = SettingsStore.Normalize(new AppSettings(UnreadAgentWorks: [sameIdCodex, claudeCompletion]));
    Assert(combinedSettings.UnreadAgentWorks!.Count == 2, "persisted unread identity includes provider");
    var combinedRows = new MainViewModel();
    combinedRows.ApplyUnreadCompletedAgents([sameIdCodex, claudeCompletion]);
    combinedRows.ApplyAgents(restartedClaude.ActiveAgents, claudeTestNow.AddMinutes(2), false);
    Assert(combinedRows.AgentItems.Count(row => row.ThreadId == "named") == 2 && combinedRows.AgentItems.Any(row => row.Provider == AgentProvider.Codex && row.IsCompleted), "Claude reactivation never consumes a same-id Codex completion");
    Assert(combinedRows.MarkAllCompletedAgentsRead() && !combinedRows.HasUnreadCompletedAgents && combinedRows.HasActiveAgents, "mark all read clears both providers and preserves active work");

    // OAuth requests use a fake transport; production DPAPI and atomic persistence are real.
    var tokensPath = Path.Combine(claudeFixtureRoot, "claude-tokens.dat");
    var protectedStore = new ClaudeTokenStore(tokensPath);
    var originalTokens = new ClaudeTokens("test-access", "test-refresh", claudeTestNow.AddHours(1));
    protectedStore.Save(originalTokens);
    Assert(protectedStore.Load() == originalTokens && !Encoding.UTF8.GetString(File.ReadAllBytes(tokensPath)).Contains("test-access"), "CurrentUser DPAPI round trip encrypts token bytes");
    protectedStore.Save(originalTokens with { RefreshToken = "rotated-refresh" });
    Assert(protectedStore.Load()!.RefreshToken == "rotated-refresh" && !Directory.GetFiles(claudeFixtureRoot, "*.tmp").Any(), "atomic credential replacement leaves rotated refresh persisted and no temporary files");
    protectedStore.Delete();
    Assert(protectedStore.Load() is null, "disconnect removes the independent token file");
    File.WriteAllBytes(tokensPath, [1, 2, 3]);
    Assert(protectedStore.Load() is null, "corrupt DPAPI data fails closed without exposing it");
    protectedStore.Delete();

    var attempt = new ClaudeOAuthAttempt(ClaudeOAuthConstants.ManualRedirect);
    var query = ParseQuery(attempt.AuthorizeUri);
    using (var hash = SHA256.Create())
    {
        var challenge = Convert.ToBase64String(hash.ComputeHash(Encoding.ASCII.GetBytes(attempt.Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert(attempt.Verifier.Length == 43 && attempt.State.Length == 43 && query["code_challenge"] == challenge && query["code_challenge_method"] == "S256", "PKCE verifier is 32 random bytes and the challenge is SHA256 base64url");
    }
    Assert(attempt.AuthorizeUri.GetLeftPart(UriPartial.Path) == ClaudeOAuthConstants.AuthorizeUrl && query["client_id"] == ClaudeOAuthConstants.ClientId && query["code"] == "true" && query["response_type"] == "code" && query["redirect_uri"] == ClaudeOAuthConstants.ManualRedirect && query["scope"] == ClaudeOAuthConstants.Scopes && query["state"] == attempt.State, "authorize URL carries exactly the required OAuth parameters");
    Assert(attempt.TryParseManualCode("sample-code#" + attempt.State, out var parsedCode) && parsedCode == "sample-code" && !attempt.TryParseManualCode("sample-code#wrong", out _) && !attempt.TryParseManualCode("sample-code", out _) && !attempt.TryParseManualCode("#" + attempt.State, out _), "manual code parser accepts code#state and rejects mismatched or missing state/code");
    Assert(new ClaudeOAuthAttempt("http://localhost:1234/callback").State != attempt.State, "each attempt receives fresh state");
    Assert(attempt.TryValidateCallback("GET", "/callback", "test-code", attempt.State, out var callbackCode) && callbackCode == "test-code" &&
        !attempt.TryValidateCallback("GET", "/callback", "test-code", "wrong", out _) &&
        !attempt.TryValidateCallback("GET", "/other", "test-code", attempt.State, out _) &&
        !attempt.TryValidateCallback("POST", "/callback", "test-code", attempt.State, out _) &&
        !attempt.TryValidateCallback("GET", "/callback", null, attempt.State, out _), "callback validates method, route, code and state independently of listener availability");
    if (HttpListener.IsSupported)
    {
        using (var callback = new ClaudeOAuthCallback())
        using (var loopbackHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
        using (var callbackCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var callbackTask = callback.WaitForCodeAsync("You can close this window.", "Invalid callback.", callbackCancellation.Token);
            using var rejectedCallback = await loopbackHttp.GetAsync(callback.Attempt.RedirectUri + "?code=test-code&state=wrong", callbackCancellation.Token);
            Assert(rejectedCallback.StatusCode == HttpStatusCode.BadRequest && !callbackTask.IsCompleted, "loopback callback rejects a mismatched state without authorizing it");
            using var acceptedCallback = await loopbackHttp.GetAsync(callback.Attempt.RedirectUri + "?code=test-code&state=" + callback.Attempt.State, callbackCancellation.Token);
            Assert(acceptedCallback.IsSuccessStatusCode && await callbackTask == "test-code" && (await acceptedCallback.Content.ReadAsStringAsync()).Contains("You can close this window."), "loopback callback accepts matching state and returns a close-window page");
        }
        using (var cancelledCallback = new ClaudeOAuthCallback())
        using (var cancelCallback = new CancellationTokenSource())
        {
            var callbackTask = cancelledCallback.WaitForCodeAsync("done", "invalid", cancelCallback.Token);
            cancelCallback.Cancel();
            var observedCancellation = false;
            try { await callbackTask; } catch (OperationCanceledException) { observedCancellation = true; }
            Assert(observedCancellation, "cancelling OAuth stops the pending listener");
        }
    }
    else Console.WriteLine("Loopback smoke unavailable: HttpListener.IsSupported=false; callback validation and manual OAuth are covered.");
    var memoryTokens = new TestClaudeTokenStore();
    var usagePath = Path.Combine(claudeFixtureRoot, "usage.json");
    var fakeOAuth = new TestHttpHandler(async (request, call) =>
    {
        if (call == 1)
        {
            Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == ClaudeOAuthConstants.TokenUrl, "code exchange uses POST token endpoint");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var payload = body.RootElement;
            Assert(payload.GetProperty("grant_type").GetString() == "authorization_code" && payload.GetProperty("code").GetString() == "sample-code" && payload.GetProperty("redirect_uri").GetString() == attempt.RedirectUri && payload.GetProperty("code_verifier").GetString() == attempt.Verifier && payload.GetProperty("state").GetString() == attempt.State && payload.GetProperty("client_id").GetString() == ClaudeOAuthConstants.ClientId, "code exchange JSON carries PKCE, state and redirect");
            return JsonResponse(HttpStatusCode.OK, """{"access_token":"issued-access","refresh_token":"issued-refresh","expires_in":3600}""");
        }
        Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "issued-access" && request.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20" && request.Headers.UserAgent.ToString() == "codex-tracker/0.22.0", "usage GET carries required headers and product version");
        return JsonResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":25,"resets_at":"2026-10-01T14:00:00Z"},"seven_day":{"utilization":60,"resets_at":null}}""");
    });
    using (var client = new ClaudeUsageClient(memoryTokens, usagePath, "0.22.0", fakeOAuth, () => claudeTestNow))
    {
        await client.ExchangeCodeAsync(attempt, parsedCode, CancellationToken.None);
        Assert(client.State == ClaudeConnectionState.Connected && memoryTokens.Load()!.RefreshToken == "issued-refresh", "code exchange persists independent credentials");
        await client.RefreshAsync(CancellationToken.None);
        Assert(!client.IsStale && client.Snapshot!.Windows.Count == 2 && client.Snapshot.Windows[0].RemainingPercent == 75, "usage poll publishes and caches official remaining quota");
        await client.RefreshAsync(CancellationToken.None, onlyIfStale: true);
        Assert(fakeOAuth.Calls == 2, "fresh engagement does not duplicate usage polling");
    }
    using (var restartedClient = new ClaudeUsageClient(memoryTokens, usagePath, "0.22.0", new TestHttpHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))), () => claudeTestNow))
        Assert(restartedClient.IsStale && restartedClient.Snapshot!.Windows.Count == 2, "restart loads last quota with explicit stale state");
    var cancellationStore = new TestClaudeTokenStore();
    using (var cancelledLogin = new CancellationTokenSource())
    using (var cancelledClient = new ClaudeUsageClient(cancellationStore, Path.Combine(claudeFixtureRoot, "cancelled.json"), "0.22.0",
        new TestHttpHandler((_, _) => { cancelledLogin.Cancel(); return Task.FromResult(JsonResponse(HttpStatusCode.OK, """{"access_token":"late","refresh_token":"late","expires_in":3600}""")); }), () => claudeTestNow))
    {
        var observedCancellation = false;
        try { await cancelledClient.ExchangeCodeAsync(attempt, "sample-code", cancelledLogin.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        Assert(observedCancellation && cancellationStore.Load() is null && cancelledClient.State == ClaudeConnectionState.Disconnected, "cancelled login cannot persist tokens returned late by HTTP");
    }
    foreach (var expiredSoon in new[] { true, false })
    {
        var rotationStore = new TestClaudeTokenStore(new("old-access", "old-refresh", expiredSoon ? claudeTestNow.AddMinutes(4) : claudeTestNow.AddHours(1)));
        var refreshTransport = new TestHttpHandler(async (request, call) =>
        {
            if (request.RequestUri!.AbsoluteUri == ClaudeOAuthConstants.TokenUrl)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert(body.RootElement.GetProperty("grant_type").GetString() == "refresh_token" && body.RootElement.GetProperty("refresh_token").GetString() == "old-refresh" && body.RootElement.GetProperty("client_id").GetString() == ClaudeOAuthConstants.ClientId, "refresh request uses stored independent refresh token");
                return JsonResponse(HttpStatusCode.OK, """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
            }
            if (!expiredSoon && call == 1) return JsonResponse(HttpStatusCode.Unauthorized, "{}");
            Assert(rotationStore.Load()!.RefreshToken == "new-refresh" && request.Headers.Authorization!.Parameter == "new-access", "rotation is persisted before retrying usage with new access");
            return JsonResponse(HttpStatusCode.OK, "{}");
        });
        using var refreshedClient = new ClaudeUsageClient(rotationStore, Path.Combine(claudeFixtureRoot, "rotation-" + expiredSoon + ".json"), "0.22.0", refreshTransport, () => claudeTestNow);
        await refreshedClient.RefreshAsync(CancellationToken.None);
        Assert(refreshedClient.State == ClaudeConnectionState.Connected && !refreshedClient.IsStale && refreshTransport.Calls == (expiredSoon ? 2 : 3), "refresh before expiry and retry after 401 both succeed");
    }
    foreach (var failureStatus in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized })
    {
        var rejectedStore = new TestClaudeTokenStore(new("old", "revoked", claudeTestNow.AddMinutes(1)));
        using var rejectedClient = new ClaudeUsageClient(rejectedStore, Path.Combine(claudeFixtureRoot, "rejected.json"), "0.22.0",
            new TestHttpHandler((_, _) => Task.FromResult(JsonResponse(failureStatus, """{"error":"invalid_grant"}"""))), () => claudeTestNow);
        await rejectedClient.RefreshAsync(CancellationToken.None);
        Assert(rejectedClient.State == ClaudeConnectionState.Reconnect && rejectedStore.Load() is null && rejectedClient.IsStale, "invalid_grant/400/401 refresh requires reconnect and removes invalid credentials");
    }
    var retryRejectedStore = new TestClaudeTokenStore(originalTokens);
    foreach (var rejectedUsageStatus in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized })
    {
        var preemptiveStore = new TestClaudeTokenStore(originalTokens with { ExpiresAt = claudeTestNow.AddMinutes(1) });
        var preemptiveTransport = new TestHttpHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? JsonResponse(HttpStatusCode.OK, """{"access_token":"new","refresh_token":"rotated","expires_in":3600}""")
            : JsonResponse(rejectedUsageStatus, "{}")));
        using var preemptiveClient = new ClaudeUsageClient(preemptiveStore, Path.Combine(claudeFixtureRoot, "preemptive.json"), "0.22.0", preemptiveTransport, () => claudeTestNow);
        await preemptiveClient.RefreshAsync(CancellationToken.None);
        Assert(preemptiveClient.State == ClaudeConnectionState.Reconnect && preemptiveStore.Load() is null && preemptiveTransport.Calls == 2, "400/401 after preemptive refresh requires reconnect without a second refresh loop");
    }
    using (var retryRejected = new ClaudeUsageClient(retryRejectedStore, usagePath, "0.22.0", new TestHttpHandler((request, _) => Task.FromResult(
        request.Method == HttpMethod.Post ? JsonResponse(HttpStatusCode.OK, """{"access_token":"new","refresh_token":"rotated","expires_in":3600}""") : JsonResponse(HttpStatusCode.Unauthorized, "{}"))), () => claudeTestNow))
    {
        await retryRejected.RefreshAsync(CancellationToken.None);
        Assert(retryRejected.State == ClaudeConnectionState.Reconnect && retryRejectedStore.Load() is null, "401 after successful token refresh disconnects without looping");
    }
    var backoffNow = claudeTestNow;
    var backoffHandler = new TestHttpHandler((_, call) => Task.FromResult(JsonResponse(call % 2 == 1 ? (HttpStatusCode)429 : HttpStatusCode.ServiceUnavailable, "{}")));
    using (var backoffClient = new ClaudeUsageClient(new TestClaudeTokenStore(originalTokens), Path.Combine(claudeFixtureRoot, "backoff.json"), "0.22.0", backoffHandler, () => backoffNow))
    {
        foreach (var seconds in new[] { 60, 120, 240, 480, 600, 600 })
        {
            await backoffClient.RefreshAsync(CancellationToken.None);
            Assert(backoffClient.NextAttemptAt == backoffNow.AddSeconds(seconds) && backoffClient.IsStale, "429/5xx exponential backoff caps at ten minutes");
            var calls = backoffHandler.Calls;
            await backoffClient.RefreshAsync(CancellationToken.None, onlyIfStale: true);
            Assert(backoffHandler.Calls == calls, "engagement honors backoff");
            backoffNow = backoffClient.NextAttemptAt;
        }
    }
    using (var disconnectClient = new ClaudeUsageClient(new TestClaudeTokenStore(originalTokens), usagePath, "0.22.0", new TestHttpHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, "{}"))), () => claudeTestNow))
    {
        await disconnectClient.DisconnectAsync();
        Assert(disconnectClient.State == ClaudeConnectionState.Disconnected && disconnectClient.Snapshot is null && !File.Exists(usagePath), "disconnect deletes tokens and prior-account quota cache");
    }
}
finally { Directory.Delete(claudeFixtureRoot, true); }

Assert(CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\Program Files\WindowsApps\OpenAI.Codex_1\app\ChatGPT.exe") && !CodexDesktopWindowMonitor.IsCodexDesktopExecutable(@"C:\cli\codex.exe"), "Codex executable detection remains unchanged");
Assert(CodexDesktopWindowMonitor.IsClaudeDesktopExecutable(@"C:\Program Files\WindowsApps\Claude_1\app\Claude.exe") && CodexDesktopWindowMonitor.IsClaudeDesktopExecutable(@"C:\Users\user\AppData\Local\AnthropicClaude\app-1\Claude.exe") && !CodexDesktopWindowMonitor.IsClaudeDesktopExecutable(@"C:\Users\user\AppData\Roaming\Claude\claude-code\1\claude.exe") && !CodexDesktopWindowMonitor.IsClaudeDesktopExecutable(@"C:\random\Claude.exe"), "Claude desktop detection admits MSIX and Squirrel but excludes CLI and arbitrary namesakes");
Assert(CodexDesktopWindowMonitor.ObserveForeground(@"C:\Program Files\WindowsApps\Claude_1\Claude.exe", true, false, false).Provider == AgentProvider.Claude && CodexDesktopWindowMonitor.ObserveForeground(@"C:\Program Files\WindowsApps\Claude_1\Claude.exe", false, false, false).Provider is null && CodexDesktopWindowMonitor.ObserveForeground(@"C:\Program Files\WindowsApps\Claude_1\Claude.exe", true, true, false).Provider is null, "Claude monitor rejects invisible/cloaked windows like Codex");
using (var usageDocument = JsonDocument.Parse("""{"five_hour":{"utilization":0,"resets_at":"2026-10-01T15:00:00+03:00"},"seven_day":{"utilization":100,"resets_at":"invalid"},"ignored":{"utilization":99}}"""))
{
    var usage = ClaudeUsageParser.Parse(usageDocument.RootElement, claudeTestNow);
    Assert(usage.Windows.Count == 2 && usage.Windows[0].Id == "claude:five_hour" && usage.Windows[0].WindowDurationMins == 300 && usage.Windows[0].RemainingPercent == 100 && usage.Windows[0].ResetsAt == claudeTestNow && usage.Windows[1].Id == "claude:seven_day" && usage.Windows[1].WindowDurationMins == 10080 && usage.Windows[1].RemainingPercent == 0 && usage.Windows[1].ResetsAt is null, "usage parser reads boundary values, durations and reset offsets; ignores extra keys and invalid reset");
}
using (var nullUsageDocument = JsonDocument.Parse("""{"five_hour":null,"seven_day":{"utilization":null,"resets_at":"2026-10-01T12:00:00Z"}}"""))
{
    var unknownQuota = ClaudeUsageParser.Parse(nullUsageDocument.RootElement, claudeTestNow).Windows.Single();
    Assert(!unknownQuota.HasUsage && QuotaPresentation.FormatWeeklyRemaining(unknownQuota) == "--" && unknownQuota.ResetsAt == claudeTestNow, "null utilization preserves known reset while displaying unknown percentage");
}
foreach (var json in new[] { "{}", "null", "[]", """{"five_hour":null}""", """{"five_hour":{"utilization":-1},"seven_day":{"utilization":101}}""", """{"five_hour":{"utilization":"50"},"seven_day":{}}""" })
{
    using var document = JsonDocument.Parse(json);
    Assert(ClaudeUsageParser.Parse(document.RootElement, claudeTestNow).Windows.Count == 0, "unknown/missing/null/malformed utilization stays unknown rather than fabricating zero usage");
}
foreach (var key in new[] { "ClaudeProfile", "ClaudeAccentColor", "ClaudeConnected", "ClaudeDisconnected", "ClaudeReconnect", "ClaudeConnect", "ClaudeConnectCode", "ClaudeManualFallback", "ClaudeDisconnect", "ClaudeConnectSettings", "ClaudeStale", "ClaudeWaitingBrowser", "ClaudePasteCode", "ClaudeSubmitCode", "ClaudeInvalidCode", "ClaudeLoginFailed", "ClaudeLoginCancelled", "ClaudeBrowserDone", "ClaudeInvalidCallback", "ProfileQuotaIndicator" })
    Assert(LocalizationManager.HasTextKey(key), "Claude UI text exists in both languages: " + key);
Assert(mainWindowXaml.Contains("ProgressBrush=\"{Binding CompactLeftBrush}\"") && mainWindowXaml.Contains("ProgressBrush=\"{Binding CompactRightBrush}\"") && mainWindowXaml.Contains("Binding IsClaude") && mainWindowXaml.Contains("ClaudeDetailBlock") && mainWindowXaml.Contains("Click=\"ConnectClaudeWithCode\""), "UI binds profile colors, Claude rows, detail and manual fallback");

var claudeUiRoot = Path.Combine(Path.GetTempPath(), "codex-tracker-claude-ui-" + Guid.NewGuid().ToString("N"));
Exception? claudeUiFailure = null;
var claudeUiThread = new Thread(() =>
{
    CodexTracker.App? application = null;
    CodexTracker.MainWindow? window = null;
    try
    {
        application = new CodexTracker.App();
        application.InitializeComponent();
        window = new CodexTracker.MainWindow(demo: true, settingsStore: new SettingsStore(Path.Combine(claudeUiRoot, "settings.json")));
        var vm = (MainViewModel)window.DataContext;
        vm.ApplyQuota(new(profileCodexWindows, null, null, null, claudeTestNow));
        vm.ApplyClaude(new(profileClaudeWindows, null, null, null, claudeTestNow), ClaudeConnectionState.Connected, true);
        vm.SetProfileActivity(new(true, false, false, false), new(false, false, true, false));
        window.Measure(new System.Windows.Size(window.Width, window.Height));
        window.Arrange(new System.Windows.Rect(0, 0, window.Width, window.Height));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var leftHost = (System.Windows.Controls.Grid)window.FindName("CompactFiveHourGauge");
        var rightHost = (System.Windows.Controls.Grid)window.FindName("CompactWeeklyGauge");
        var leftGauge = leftHost.Children.OfType<CircularQuotaGauge>().Single();
        var rightGauge = rightHost.Children.OfType<CircularQuotaGauge>().Single();
        Assert(leftHost.Visibility == System.Windows.Visibility.Visible && rightHost.Visibility == System.Windows.Visibility.Visible && leftGauge.Value == 20 && rightGauge.Value == 30, "real WPF compact bindings render Codex left and Claude right with independent values");
        Assert(((System.Windows.Media.SolidColorBrush)leftGauge.ProgressBrush).Color != ((System.Windows.Media.SolidColorBrush)rightGauge.ProgressBrush).Color, "real WPF gauges receive independent theme-adjusted profile brushes");
        Assert(leftHost.Children.OfType<System.Windows.Controls.TextBlock>().Select(block => block.Text).Contains("7d") && rightHost.Children.OfType<System.Windows.Controls.TextBlock>().Select(block => block.Text).Contains("5h"), "real WPF labels follow each selected window");
        Assert(!vm.IsCodexWorkAnimationEnabled && vm.CompactRightTooltip.Contains(LocalizationManager.Text("ClaudeStale")), "Claude-only work never animates the detailed Codex gauge and stale cache is identified in compact tooltips");
        vm.SetProfileColors("Escuro", "#FFB000", "#D97757");
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(((System.Windows.Media.SolidColorBrush)rightGauge.ProgressBrush).Color ==
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(AccentPalette.Create("#D97757", true).AccentHex), "Claude gauge uses the same contrast-safe dark AccentPalette as Codex");
        vm.SetProfileActivity(default, new(true, false, false, false));
        vm.SetCompactQuotaDisplay("7d");
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(leftHost.Visibility == System.Windows.Visibility.Collapsed && rightGauge.Value == 60, "single Claude display uses its permitted quota without changing Codex detail analytics");
        vm.Expanded = true;
        var claudeDetail = (System.Windows.Controls.Border)window.FindName("ClaudeDetailBlock");
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(claudeDetail.Visibility == System.Windows.Visibility.Visible && vm.ClaudeDetailStatus == LocalizationManager.Text("ClaudeStale") && vm.Weekly == "20%", "detailed UI retains Codex data and identifies stale Claude quota");
        vm.SetClaudeEnabled(false);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert(claudeDetail.Visibility == System.Windows.Visibility.Collapsed, "disabled profile hides the Claude detail block");
        vm.ApplyClaude(null, ClaudeConnectionState.Disconnected, true);
        Assert(vm.ClaudeDetailStatus == LocalizationManager.Text("ClaudeConnectSettings"), "disconnected Claude detail provides the settings connection instruction");
    }
    catch (Exception error) { claudeUiFailure = error; }
    finally { window?.Close(); application?.Shutdown(); }
});
claudeUiThread.SetApartmentState(ApartmentState.STA);
claudeUiThread.Start();
claudeUiThread.Join();
if (Directory.Exists(claudeUiRoot)) Directory.Delete(claudeUiRoot, true);
if (claudeUiFailure is not null) throw new InvalidOperationException("Claude WPF smoke failed", claudeUiFailure);

Console.WriteLine("All CodexTracker core tests passed (including Claude engagement, sessions, OAuth, DPAPI, usage and WPF bindings).");

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Test failed: " + message); }
static void WriteClaudeSession(string root, int pid, string sessionId, string status, string kind, string? name, string? cwd, DateTimeOffset at) =>
    File.WriteAllText(Path.Combine(root, pid + ".json"), JsonSerializer.Serialize(new { pid, sessionId, status, kind, name, cwd, entrypoint = "desktop", updatedAt = at.ToUnixTimeMilliseconds(), statusUpdatedAt = at.ToUnixTimeMilliseconds() }));
static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=')).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
static int CachedRolloutCount(AgentActivityService service) =>
    ((System.Collections.IDictionary)(typeof(AgentActivityService).GetField("_cache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(service)
        ?? throw new MissingFieldException(typeof(AgentActivityService).FullName, "_cache"))).Count;
static bool NearlyEqual(double? actual, double expected) => actual is double value && Math.Abs(value - expected) < 0.000001;
static bool NearlyEqualInstant(DateTimeOffset? actual, DateTimeOffset expected) => actual is { } value && Math.Abs((value - expected).TotalMilliseconds) < 1;
static string FindRepositoryFile(params string[] segments)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine([directory.FullName, .. segments]);
        if (File.Exists(candidate)) return candidate;
    }
    throw new FileNotFoundException("Repository source file not found.", Path.Combine(segments));
}

static void CopyFileSnapshot(string sourcePath, string destinationPath)
{
    using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    var remaining = source.Length;
    var buffer = new byte[128 * 1024];
    while (remaining > 0)
    {
        var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        if (read == 0) break;
        destination.Write(buffer, 0, read);
        remaining -= read;
    }
}

sealed class TestClaudeTokenStore(ClaudeTokens? initial = null) : IClaudeTokenStore
{
    private ClaudeTokens? _tokens = initial;
    public ClaudeTokens? Load() => _tokens;
    public void Save(ClaudeTokens tokens) => _tokens = tokens;
    public void Delete() => _tokens = null;
}
sealed class TestHttpHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, ++Calls);
}
