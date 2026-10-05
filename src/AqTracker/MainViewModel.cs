using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using AqTracker.Core;

namespace AqTracker;

public sealed record RankingRow(string Model, string Tokens, double Fraction, bool Priced, string SecondaryText, string Tooltip, TokenUsageTooltip TooltipData);
public sealed record ChatDetailsRow(string Title, string CachedReadTokens, string CachedReadCost, string InputTokens, string InputCost, string OutputTokens, string OutputCost, string ReasoningTokens, string ReasoningCost, string TotalTokens, string TotalCost, string EstimateNote, double CachedReadFraction, double InputFraction, double OutputFraction, double ReasoningFraction, double TotalFraction);
public sealed class ChatDetailsProjectRow : INotifyPropertyChanged
{
    private readonly IReadOnlyList<ChatDetailsRow> _allChats;
    private bool _isExpanded, _isVisible = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Project { get; }
    public string Key { get; }
    public ObservableCollection<ChatDetailsRow> Chats { get; } = [];
    public bool IsExpanded { get => _isExpanded; private set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); RefreshMaterialized(); } }
    public bool IsVisible { get => _isVisible; private set { if (_isVisible == value) return; _isVisible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible))); } }
    private IReadOnlyList<ChatDetailsRow> _filteredChats = [];
    public ChatDetailsProjectRow(string key, string project, IReadOnlyList<ChatDetailsRow> chats) { Key = key; Project = project; _allChats = chats; _filteredChats = chats; }
    public void Toggle() => IsExpanded = !IsExpanded;
    public void SetExpanded(bool value) => IsExpanded = value;
    public void ApplySearch(string? query)
    {
        var search = query?.Trim() ?? "";
        var projectMatch = Project.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0;
        _filteredChats = string.IsNullOrWhiteSpace(search) ? _allChats : projectMatch ? _allChats : _allChats.Where(chat => chat.Title.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
        IsVisible = _filteredChats.Count > 0;
        IsExpanded = false;
        Chats.Clear();
        RefreshMaterialized();
    }
    private void RefreshMaterialized()
    {
        Chats.Clear();
        if (!IsExpanded || !IsVisible) return;
        foreach (var chat in _filteredChats) Chats.Add(chat);
    }
}
public sealed class AgentActivityRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string? _parentThreadId;
    private int _hierarchyDepth;
    private Thickness _indent;
    private string _sourceType = "", _sourceTitle = "", _sourceStatus = "";
    private string _type = "", _title = "", _status = "", _modelAndEffort = "", _elapsed = "", _projectKey = "", _projectName = "Sem projeto", _separatorLabel = "";
    private bool _isWorkAnimationEnabled, _isNew, _isCompleted, _showsProjectSeparator;
    public AgentActivityRow(ActiveAgent agent, DateTimeOffset now, bool isNew)
    {
        ThreadId = agent.ThreadId;
        _isNew = isNew;
        Update(agent, now);
    }

    public AgentActivityRow(CompletedAgentWork work)
    {
        ThreadId = work.ThreadId;
        CompletionId = work.CompletionId;
        Update(work);
    }

    public void Update(CompletedAgentWork work)
    {
        Provider = work.Provider;
        HostSessionId = work.HostSessionId;
        CompletionId = work.CompletionId;
        _sourceType = work.Type;
        _sourceTitle = work.Title;
        _sourceStatus = work.Status;
        Set(ref _isCompleted, true, nameof(IsCompleted));
        RefreshLocalization();
        Set(ref _modelAndEffort, $"{work.Model} · {work.Effort}", nameof(ModelAndEffort));
        Set(ref _elapsed, FormatElapsed(work.CompletedAt - work.StartedAt), nameof(Elapsed));
        SetProject(work.ProjectPath);
    }

    public string ThreadId { get; }
    public AgentProvider Provider { get; private set; }
    public string? HostSessionId { get; private set; }
    public string IdentityKey => Provider + ":" + ThreadId;
    public bool IsClaude => Provider == AgentProvider.Claude;
    public string? CompletionId { get; private set; }
    public string? ParentThreadId => _parentThreadId;
    public int HierarchyDepth => _hierarchyDepth;
    public Thickness Indent => _indent;
    public string Type => _type;
    public string Title => _title;
    public string Status => _status;
    public string ModelAndEffort => _modelAndEffort;
    public string Elapsed => _elapsed;
    public bool IsWorkAnimationEnabled => _isWorkAnimationEnabled;
    public bool IsNew => _isNew;
    public bool IsCompleted => _isCompleted;
    public string ProjectKey => _projectKey;
    public string ProjectName => _projectName;
    public bool ShowsProjectSeparator => _showsProjectSeparator;
    public string SeparatorLabel => _separatorLabel;

    public void Update(ActiveAgent agent, DateTimeOffset now)
    {
        Provider = agent.Provider;
        HostSessionId = agent.HostSessionId;
        CompletionId = null;
        Set(ref _parentThreadId, agent.ParentThreadId, nameof(ParentThreadId));
        Set(ref _hierarchyDepth, agent.HierarchyDepth, nameof(HierarchyDepth));
        Set(ref _indent, new Thickness(Math.Min(48d, agent.HierarchyDepth * 12d), 0, 0, 0), nameof(Indent));
        _sourceType = agent.Type;
        _sourceTitle = agent.Title;
        _sourceStatus = agent.Status;
        RefreshLocalization();
        Set(ref _modelAndEffort, $"{agent.Model} · {agent.Effort}", nameof(ModelAndEffort));
        Set(ref _elapsed, FormatElapsed(now - agent.StartedAt), nameof(Elapsed));
        Set(ref _isWorkAnimationEnabled, SystemParameters.ClientAreaAnimation, nameof(IsWorkAnimationEnabled));
        Set(ref _isCompleted, false, nameof(IsCompleted));
        SetProject(agent.ProjectPath);
    }

    public void UpdateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _sourceTitle = title.Trim();
        Set(ref _title, LocalizationManager.TranslateKnown(_sourceTitle), nameof(Title));
    }

    public void MarkStable() => Set(ref _isNew, false, nameof(IsNew));
    public void SetProjectSeparator(bool value, string label)
    {
        Set(ref _showsProjectSeparator, value, nameof(ShowsProjectSeparator));
        Set(ref _separatorLabel, label, nameof(SeparatorLabel));
    }
    public void RefreshLocalization()
    {
        Set(ref _type, LocalizationManager.TranslateKnown(_sourceType), nameof(Type));
        Set(ref _title, LocalizationManager.TranslateKnown(_sourceTitle), nameof(Title));
        Set(ref _status, LocalizationManager.TranslateKnown(_sourceStatus), nameof(Status));
    }
    private void Set<T>(ref T field, T value, string name) { if (!EqualityComparer<T>.Default.Equals(field, value)) { field = value; PropertyChanged?.Invoke(this, new(name)); } }
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m" : $"{elapsed.Minutes}m {elapsed.Seconds:00}s";
    }

    private void SetProject(string? projectPath)
    {
        var normalized = string.IsNullOrWhiteSpace(projectPath) ? "" : projectPath!.Trim().TrimEnd('\\', '/');
        var label = "Sem projeto";
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            var separator = normalized.LastIndexOfAny(['\\', '/']);
            label = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
            if (string.IsNullOrWhiteSpace(label)) label = "Sem projeto";
        }
        Set(ref _projectKey, normalized, nameof(ProjectKey));
        Set(ref _projectName, label, nameof(ProjectName));
    }
}

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _weekly = "--", _fiveHour = "--", _fiveHourReset = "", _weeklyTokens = "--", _weeklyCost = "--", _reset = LocalizationManager.Text("LoadingWeeklyQuota"), _today = "--", _month = "--", _cost = "--", _todayCost = "--", _monthCost = "--", _coverage = "", _forecast = LocalizationManager.Text("InsufficientData"), _status = LocalizationManager.Text("Loading"), _currencyCode = "BRL", _compactQuotaDisplay = "both";
    private bool _expanded, _topmost, _isExhaustionRisk;
    private bool _hasActiveAgents, _hasUnreadCompletedAgents, _isAgentListOpen;
    private bool _isUpdateDialogOpen, _isUpdateDownloading;
    private double _updateProgress;
    private string _updateAvailableVersion = "", _updateStatusMessage = "", _updateCheckFeedback = "";
    private int _activeAgentCount, _unreadCompletedAgentCount;
    private double _remainingPercent, _fiveHourRemainingPercent;
    private bool _hasFiveHourQuota;
    private long? _weeklyTokenCount;
    private decimal _lastTodayUsd, _lastTodayBrl, _lastMonthUsd, _lastMonthBrl;
    private UsageWindowEstimate? _lastWeeklyEstimate;
    private UsageAnalytics? _lastAnalytics;
    private QuotaWindow? _lastWeekly;
    private WeeklyForecast? _lastForecast;
    private readonly QuotaRateHistory _rateHistory = new();
    private bool _hasQuotaSnapshot;
    private (DateTimeOffset Start, DateTimeOffset End)? _activeQuotaCycle;
    private RankingPeriod _rankingPeriod = RankingPeriod.Month;
    private bool _hasAnalytics;
    private bool _hasVisibleChatProjects;
    private readonly QuotaSnapshotStore _quotaSnapshotStore;
    private readonly Func<DateTimeOffset> _clock;
    public MainViewModel(QuotaSnapshotStore? quotaSnapshotStore = null, Func<DateTimeOffset>? clock = null) { _quotaSnapshotStore = quotaSnapshotStore ?? new QuotaSnapshotStore(persistent: false); _clock = clock ?? (() => DateTimeOffset.Now); }
    private string _chatSearch = "";
    private IReadOnlyList<AgentActivityRow> _completedAgentRows = [];
    public ObservableCollection<RankingRow> Ranking { get; } = [];
    public ObservableCollection<AgentActivityRow> ActiveAgents { get; } = [];
    public ObservableCollection<AgentActivityRow> AgentItems { get; } = [];
    public ObservableCollection<ChatDetailsProjectRow> ChatProjects { get; } = [];
    public bool HasVisibleChatProjects { get => _hasVisibleChatProjects; private set => Set(ref _hasVisibleChatProjects, value); }
    public string ChatSearch
    {
        get => _chatSearch;
        set
        {
            if (EqualityComparer<string>.Default.Equals(_chatSearch, value)) return;
            Set(ref _chatSearch, value);
            foreach (var project in ChatProjects) project.ApplySearch(value);
            UpdateVisibleChatProjects();
        }
    }
    public void ResetChatDetailsView()
    {
        if (!string.IsNullOrEmpty(_chatSearch)) Set(ref _chatSearch, "", nameof(ChatSearch));
        foreach (var project in ChatProjects) project.ApplySearch("");
        UpdateVisibleChatProjects();
    }
    public string AppVersion { get; } = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";
    public string Weekly { get => _weekly; set => Set(ref _weekly, value); }
    public string FiveHour { get => _fiveHour; private set => Set(ref _fiveHour, value); }
    public string FiveHourReset { get => _fiveHourReset; private set => Set(ref _fiveHourReset, value); }
    public string WeeklyTokens { get => _weeklyTokens; private set => Set(ref _weeklyTokens, value); }
    public long? WeeklyTokenCount { get => _weeklyTokenCount; private set => Set(ref _weeklyTokenCount, value); }
    public string WeeklyCost { get => _weeklyCost; private set => Set(ref _weeklyCost, value); }
    public IReadOnlyList<DailyTokenUsage> DailyTokenSeries { get; private set; } = [];
    public IReadOnlyList<DailyQuotaUsage> DailyQuotaSeries { get; private set; } = [];
    public string Reset { get => _reset; set => Set(ref _reset, value); }
    public string Today { get => _today; set => Set(ref _today, value); }
    public string Month { get => _month; set => Set(ref _month, value); }
    public string Cost
    {
        get => _cost;
        set
        {
            if (!EqualityComparer<string>.Default.Equals(_cost, value))
            {
                _cost = value;
                PropertyChanged?.Invoke(this, new(nameof(Cost)));
                PropertyChanged?.Invoke(this, new(nameof(CostFormatted)));
            }
        }
    }
    public string CostFormatted => Cost;
    public string TodayCost { get => _todayCost; private set => Set(ref _todayCost, value); }
    public string MonthCost { get => _monthCost; private set => Set(ref _monthCost, value); }
    public string CurrencyCode { get => _currencyCode; private set => Set(ref _currencyCode, SettingsStore.NormalizeCurrency(value)); }
    public string Coverage { get => _coverage; set => Set(ref _coverage, value); }
    public string Forecast { get => _forecast; set => Set(ref _forecast, value); }
    public bool IsExhaustionRisk { get => _isExhaustionRisk; private set => Set(ref _isExhaustionRisk, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool Topmost { get => _topmost; set => Set(ref _topmost, value); }
    public bool HasActiveAgents { get => _hasActiveAgents; private set => Set(ref _hasActiveAgents, value); }
    public bool HasUnreadCompletedAgents { get => _hasUnreadCompletedAgents; private set => Set(ref _hasUnreadCompletedAgents, value); }
    public bool CanMarkAllCompletedAgentsRead => HasUnreadCompletedAgents;
    public bool HasAgentIndicator => HasActiveAgents || HasUnreadCompletedAgents;
    public bool ShowsCompletedIndicator => !HasActiveAgents && HasUnreadCompletedAgents;
    public string AgentIndicatorTooltip => LocalizationManager.Text(ShowsCompletedIndicator ? "UnreadAgentCompletions" : "AgentsWorkingTooltip");
    public bool IsAgentListOpen { get => _isAgentListOpen; set => Set(ref _isAgentListOpen, value); }
    public bool IsWorkAnimationEnabled => HasActiveAgents && SystemParameters.ClientAreaAnimation;
    public int ActiveAgentCount { get => _activeAgentCount; private set => Set(ref _activeAgentCount, value); }
    public int UnreadCompletedAgentCount { get => _unreadCompletedAgentCount; private set => Set(ref _unreadCompletedAgentCount, value); }
    public double RemainingPercent { get => _remainingPercent; set => Set(ref _remainingPercent, value); }
    public double FiveHourRemainingPercent { get => _fiveHourRemainingPercent; private set => Set(ref _fiveHourRemainingPercent, value); }
    public bool HasFiveHourQuota { get => _hasFiveHourQuota; private set => Set(ref _hasFiveHourQuota, value); }
    public bool ShowCompactFiveHour => _profileGauges.Any(gauge => gauge.Window == "5h");
    public bool ShowCompactWeekly => _profileGauges.Any(gauge => gauge.Window == "7d");
    public int CompactQuotaCount => _profileGauges.Count;
    public bool IsUpdateDialogOpen { get => _isUpdateDialogOpen; set => Set(ref _isUpdateDialogOpen, value); }
    public bool IsUpdateDownloading { get => _isUpdateDownloading; set => Set(ref _isUpdateDownloading, value); }
    public double UpdateProgress { get => _updateProgress; set => Set(ref _updateProgress, value); }
    public string UpdateStatusMessage { get => _updateStatusMessage; set => Set(ref _updateStatusMessage, value); }
    public string UpdateCheckFeedback { get => _updateCheckFeedback; set => Set(ref _updateCheckFeedback, value); }
    public string UpdateAvailableVersion
    {
        get => _updateAvailableVersion;
        set
        {
            if (EqualityComparer<string>.Default.Equals(_updateAvailableVersion, value)) return;
            _updateAvailableVersion = value;
            PropertyChanged?.Invoke(this, new(nameof(UpdateAvailableVersion)));
            PropertyChanged?.Invoke(this, new(nameof(UpdateAvailableMessage)));
        }
    }
    public string UpdateAvailableMessage => LocalizationManager.Format("UpdateAvailableMessage", UpdateAvailableVersion);
    public bool IsRankingDay { get => _rankingPeriod == RankingPeriod.Day; set { if (value) SetRankingPeriod(RankingPeriod.Day); } }
    public bool IsRankingWeek { get => _rankingPeriod == RankingPeriod.Week; set { if (value) SetRankingPeriod(RankingPeriod.Week); } }
    public bool IsRankingMonth { get => _rankingPeriod == RankingPeriod.Month; set { if (value) SetRankingPeriod(RankingPeriod.Month); } }

    public void SetCompactQuotaDisplay(string? display)
    {
        var normalized = SettingsStore.NormalizeCompactQuotaDisplay(display);
        if (_compactQuotaDisplay == normalized) return;
        _compactQuotaDisplay = normalized;
        RefreshProfilePresentation();
        PropertyChanged?.Invoke(this, new(nameof(ShowCompactFiveHour)));
        PropertyChanged?.Invoke(this, new(nameof(ShowCompactWeekly)));
        PropertyChanged?.Invoke(this, new(nameof(CompactQuotaCount)));
    }

    public void ApplyAgents(IReadOnlyList<ActiveAgent> agents, DateTimeOffset now, bool animateNewRows, bool? animationsEnabled = null)
    {
        var hasActiveAgents = agents.Count > 0;
        var existing = ActiveAgents.ToDictionary(row => row.IdentityKey, StringComparer.OrdinalIgnoreCase);
        var completedByThread = _completedAgentRows
            .GroupBy(row => row.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var activeThreadIds = agents.Select(agent => agent.Provider + ":" + agent.ThreadId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _completedAgentRows = _completedAgentRows.Where(row => !activeThreadIds.Contains(row.IdentityKey)).ToArray();
        var ordered = new List<AgentActivityRow>(agents.Count);
        foreach (var agent in agents)
        {
            if (existing.TryGetValue(agent.Provider + ":" + agent.ThreadId, out var row)) row.Update(agent, now);
            else if (completedByThread.TryGetValue(agent.Provider + ":" + agent.ThreadId, out row)) row.Update(agent, now);
            else row = new AgentActivityRow(agent, now, animateNewRows && (animationsEnabled ?? SystemParameters.ClientAreaAnimation));
            ordered.Add(row);
        }
        for (var index = 0; index < ordered.Count; index++)
        {
            if (index < ActiveAgents.Count && ReferenceEquals(ActiveAgents[index], ordered[index])) continue;
            var currentIndex = ActiveAgents.IndexOf(ordered[index]);
            if (currentIndex >= 0) ActiveAgents.Move(currentIndex, index);
            else ActiveAgents.Insert(index, ordered[index]);
        }
        while (ActiveAgents.Count > ordered.Count) ActiveAgents.RemoveAt(ActiveAgents.Count - 1);
        ActiveAgentCount = agents.Count;
        HasActiveAgents = hasActiveAgents;
        UpdateUnreadCompletedState();
        PropertyChanged?.Invoke(this, new(nameof(IsWorkAnimationEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(HasAgentIndicator)));
        PropertyChanged?.Invoke(this, new(nameof(ShowsCompletedIndicator)));
        PropertyChanged?.Invoke(this, new(nameof(AgentIndicatorTooltip)));
        RefreshAgentItems();
    }

    public void ApplyAgentTitles(IReadOnlyDictionary<string, string> titles)
    {
        foreach (var row in ActiveAgents.Where(row => row.Provider == AgentProvider.Codex))
            if (titles.TryGetValue(row.ThreadId, out var title)) row.UpdateTitle(title);
    }

    public void ApplyUnreadCompletedAgents(IReadOnlyList<CompletedAgentWork> works)
    {
        var activeThreadIds = ActiveAgents.Select(row => row.IdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = _completedAgentRows
            .GroupBy(row => row.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _completedAgentRows = works
            .Where(work => !string.Equals(work.Type, "Subagent", StringComparison.OrdinalIgnoreCase) && !activeThreadIds.Contains(work.Provider + ":" + work.ThreadId))
            .GroupBy(work => work.Provider + ":" + work.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(work => work.CompletedAt).First())
            .OrderByDescending(work => work.CompletedAt)
            .Select(work =>
            {
                if (!existing.TryGetValue(work.Provider + ":" + work.ThreadId, out var row)) return new AgentActivityRow(work);
                row.Update(work);
                return row;
            })
            .ToArray();
        UpdateUnreadCompletedState();
        RefreshAgentItems();
    }

    public bool MarkAllCompletedAgentsRead()
    {
        if (_completedAgentRows.Count == 0) return false;
        _completedAgentRows = [];
        UpdateUnreadCompletedState();
        RefreshAgentItems();
        return true;
    }

    private void UpdateUnreadCompletedState()
    {
        UnreadCompletedAgentCount = _completedAgentRows.Count;
        HasUnreadCompletedAgents = _completedAgentRows.Count > 0;
        PropertyChanged?.Invoke(this, new(nameof(HasAgentIndicator)));
        PropertyChanged?.Invoke(this, new(nameof(ShowsCompletedIndicator)));
        PropertyChanged?.Invoke(this, new(nameof(AgentIndicatorTooltip)));
        PropertyChanged?.Invoke(this, new(nameof(CanMarkAllCompletedAgentsRead)));
    }

    private void RefreshAgentItems()
    {
        var source = ActiveAgents
            .Concat(_completedAgentRows)
            .GroupBy(row => row.Provider)
            .OrderBy(platform => platform.Key)
            .SelectMany(platform => platform.GroupBy(row => row.ProjectKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderBy(row => row.IsCompleted ? 1 : 0)))
            .ToArray();
        AgentProvider? previousProvider = null;
        foreach (var row in source)
        {
            row.SetProjectSeparator(previousProvider != row.Provider, row.Provider.ToString());
            previousProvider = row.Provider;
        }
        for (var index = 0; index < source.Length; index++)
        {
            if (index < AgentItems.Count && ReferenceEquals(AgentItems[index], source[index])) continue;
            var currentIndex = AgentItems.IndexOf(source[index]);
            if (currentIndex >= 0) AgentItems.Move(currentIndex, index);
            else AgentItems.Insert(index, source[index]);
        }
        while (AgentItems.Count > source.Length) AgentItems.RemoveAt(AgentItems.Count - 1);
    }

    public void MarkNewAgentRowsStable()
    {
        foreach (var row in ActiveAgents.Where(row => row.IsNew)) row.MarkStable();
    }

    public void ApplyQuota(RateLimitSnapshot snapshot)
    {
        var weekly = OfficialCodexQuotaWindows.Weekly(snapshot.Windows);
        ApplyCompactQuotaWindows(snapshot.Windows);
        _lastWeekly = weekly;
        _hasQuotaSnapshot = true;
        UpdateActiveQuotaCycle(weekly);
        ApplyWeeklyDisplay(weekly);
        _rateHistory.Record(snapshot.Windows, snapshot.ReceivedAt);
        ApplyForecast(WeeklyForecastCalculator.Calculate(weekly, snapshot.ReceivedAt, _rateHistory.For(weekly)));
        RefreshDailyQuotaSeries(weekly, snapshot.ReceivedAt);
        Status = weekly is null ? LocalizationManager.Text("NoLiveData") : LocalizationManager.Text("LiveData");
    }

    public void SetCurrency(string? currencyCode)
    {
        CurrencyCode = CurrencyPresentation.Normalize(currencyCode);
        RefreshFormattedCosts();
        RefreshRanking();
    }

    public void Apply(RateLimitSnapshot snapshot, UsageAnalytics analytics, string? currencyCode = null)
    {
        var weekly = OfficialCodexQuotaWindows.Weekly(snapshot.Windows);
        ApplyCompactQuotaWindows(snapshot.Windows);
        _lastWeekly = weekly;
        _hasQuotaSnapshot = true;
        UpdateActiveQuotaCycle(weekly);
        ApplyWeeklyDisplay(weekly);
        Today = TokenPresentation.Format(analytics.TodayTokens, LocalizationManager.CurrentLanguageCode); Month = TokenPresentation.Format(analytics.MonthTokens, LocalizationManager.CurrentLanguageCode);
        DailyTokenSeries = analytics.DailySeries ?? [];
        PropertyChanged?.Invoke(this, new(nameof(DailyTokenSeries)));
        _lastWeeklyEstimate = analytics.EstimateInWeeklyWindow(weekly);
        _lastAnalytics = analytics;
        RefreshDailyQuotaSeries(weekly, snapshot.ReceivedAt);
        RefreshChatDetails();
        WeeklyTokenCount = _lastWeeklyEstimate?.Tokens;
        WeeklyTokens = WeeklyTokenCount is { } weeklyTokens ? TokenPresentation.Format(weeklyTokens, LocalizationManager.CurrentLanguageCode) : "--";
        _lastTodayUsd = analytics.TodayUsd; _lastTodayBrl = analytics.TodayBrl;
        _lastMonthUsd = analytics.MonthUsd; _lastMonthBrl = analytics.MonthBrl;
        _hasAnalytics = true;
        if (currencyCode is not null) CurrencyCode = CurrencyPresentation.Normalize(currencyCode);
        RefreshFormattedCosts();
        _rateHistory.Record(snapshot.Windows, snapshot.ReceivedAt);
        ApplyForecast(WeeklyForecastCalculator.Calculate(weekly, snapshot.ReceivedAt, _rateHistory.For(weekly)));
        RefreshRanking();
        Status = weekly is null ? LocalizationManager.Text("NoLiveData") : LocalizationManager.Text("LiveData");
    }

    // Shows the weekly window as the user should see it now: a cycle that ended without fresh data
    // is displayed as reset. The raw window stays in _lastWeekly for history and forecasting.
    private void ApplyWeeklyDisplay(QuotaWindow? rawWeekly)
    {
        var weekly = QuotaWindowExpiry.Normalize(rawWeekly, _clock());
        Weekly = QuotaPresentation.FormatWeeklyRemaining(weekly);
        RemainingPercent = weekly?.RemainingPercent ?? 0;
        Reset = weekly is null ? LocalizationManager.Text("QuotaNotReported") : ResetCountdown.Format(weekly, _clock(), LocalizationManager.CurrentLanguageCode);
    }

    private void ApplyCompactQuotaWindows(IEnumerable<QuotaWindow> windows)
    {
        _codexSnapshot = windows.ToArray();
        RefreshProfilePresentation();
        var fiveHour = OfficialCodexQuotaWindows.FiveHours(CodexWindows);
        HasFiveHourQuota = fiveHour is not null;
        FiveHour = QuotaPresentation.FormatWeeklyRemaining(fiveHour);
        FiveHourRemainingPercent = fiveHour?.RemainingPercent ?? 0;
        FiveHourReset = fiveHour is null ? "" : ResetCountdown.Format(fiveHour, _clock(), LocalizationManager.CurrentLanguageCode);
        PropertyChanged?.Invoke(this, new(nameof(ShowCompactFiveHour)));
        PropertyChanged?.Invoke(this, new(nameof(ShowCompactWeekly)));
        PropertyChanged?.Invoke(this, new(nameof(CompactQuotaCount)));
    }

    private void RefreshDailyQuotaSeries(QuotaWindow? weekly, DateTimeOffset receivedAt)
    {
        var now = _clock();
        var official = weekly is null ? _quotaSnapshotStore.Read() : _quotaSnapshotStore.Append(receivedAt, weekly.UsedPercent);
        var initials = weekly is null ? _quotaSnapshotStore.ReadInitial() : _quotaSnapshotStore.CaptureInitial(receivedAt, weekly.UsedPercent, now);
        var history = (_lastAnalytics?.QuotaTimeline ?? []).Concat(official).GroupBy(x => x.At).Select(group => group.Last());
        var initialByDay = initials.Where(x => x.At <= now && x.At.LocalDateTime.Date <= now.LocalDateTime.Date).ToDictionary(x => x.At.LocalDateTime.Date, x => x.UsedPercent);
        DailyQuotaSeries = AqTracker.Core.DailyQuotaSeries.CloseByLocalDay(history, now.LocalDateTime.Date, now)
            .Select(day => day with { InitialUsedPercent = initialByDay.TryGetValue(day.Day, out var initial) ? initial : null }).ToArray();
        PropertyChanged?.Invoke(this, new(nameof(DailyQuotaSeries)));
    }

    private void SetRankingPeriod(RankingPeriod period)
    {
        if (_rankingPeriod == period) return;
        _rankingPeriod = period;
        PropertyChanged?.Invoke(this, new(nameof(IsRankingDay)));
        PropertyChanged?.Invoke(this, new(nameof(IsRankingWeek)));
        PropertyChanged?.Invoke(this, new(nameof(IsRankingMonth)));
        RefreshRanking();
    }

    private void RefreshRanking()
    {
        if (_lastAnalytics is null) return;
        var now = DateTimeOffset.Now;
        IReadOnlyList<ModelUsage> models = _rankingPeriod switch
        {
            RankingPeriod.Day => _lastAnalytics.ModelsInWindow(new DateTimeOffset(now.LocalDateTime.Date, now.Offset), now),
            RankingPeriod.Week when _activeQuotaCycle is { } cycle => _lastAnalytics.ModelsInWindow(cycle.Start, cycle.End),
            RankingPeriod.Week => [],
            _ => _lastAnalytics.Models
        };
        Ranking.Clear();
        var max = Math.Max(1, models.Take(5).Select(x => x.Tokens).DefaultIfEmpty().Max());
        foreach (var item in models.Take(5))
        {
            var secondaryText = item.Priced
                ? CurrencyPresentation.FormatCost(item.CostUsd, item.CostUsd * _lastAnalytics.UsdBrl, CurrencyCode, LocalizationManager.CurrentLanguageCode)
                : LocalizationManager.Text("NoTariff");
            var displayModel = string.Equals(item.Model, "unknown", StringComparison.Ordinal)
                ? LocalizationManager.Text("UnregisteredModel")
                : item.Model;
            var tooltipData = TokenUsageTooltip.Create(displayModel, item.Breakdown ?? TokenUsageBreakdown.Zero, item.Priced, _lastAnalytics.UsdBrl, CurrencyCode);
            Ranking.Add(new(displayModel, TokenPresentation.Format(item.Tokens, LocalizationManager.CurrentLanguageCode), item.Tokens / (double)max, item.Priced, secondaryText,
                tooltipData.ToPlainText(), tooltipData));
        }
    }

    private void RefreshChatDetails()
    {
        var expandedKeys = string.IsNullOrWhiteSpace(ChatSearch)
            ? ChatProjects.Where(project => project.IsExpanded).Select(project => project.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ChatProjects.Clear();
        if (_lastAnalytics?.Chats is not { } chats) { UpdateVisibleChatProjects(); return; }
        foreach (var project in chats
            .GroupBy(chat => ProjectKey(chat.ProjectPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Key = group.Key, Name = DisplayProject(group.First().ProjectPath), Chats = group })
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = project.Chats
                .OrderByDescending(chat => chat.LastUpdatedAt)
                .ThenByDescending(chat => chat.Tokens)
                .ThenBy(chat => chat.Title ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(chat => chat.ThreadId, StringComparer.OrdinalIgnoreCase)
                .Select(FormatChat)
                .ToArray();
            var projectRow = new ChatDetailsProjectRow(project.Key, project.Name, rows);
            if (string.IsNullOrWhiteSpace(ChatSearch) && expandedKeys.Contains(project.Key)) projectRow.SetExpanded(true);
            else projectRow.ApplySearch(ChatSearch);
            ChatProjects.Add(projectRow);
        }
        UpdateVisibleChatProjects();
    }

    private ChatDetailsRow FormatChat(ChatUsage chat)
    {
        var breakdown = chat.Breakdown;
        var complete = chat.Tokens > 0 && chat.PricedTokens == chat.Tokens;
        var hasKnown = chat.PricedTokens > 0;
        string Cost(decimal cost) => hasKnown ? CurrencyPresentation.FormatCost(cost, cost * (_lastAnalytics?.UsdBrl ?? 0), CurrencyCode, LocalizationManager.CurrentLanguageCode) : LocalizationManager.Text("NoTariff");
        var note = complete ? "" : hasKnown ? LocalizationManager.Text("PartialTariffEstimate") : LocalizationManager.Text("NoTariff");
        return new ChatDetailsRow(
            string.IsNullOrWhiteSpace(chat.Title) ? LocalizationManager.Text("CodexConversation") : chat.Title!,
            TokenPresentation.Format(breakdown.CachedReadTokens, LocalizationManager.CurrentLanguageCode), Cost(breakdown.CachedReadCostUsd),
            TokenPresentation.Format(breakdown.InputTokens, LocalizationManager.CurrentLanguageCode), Cost(breakdown.InputCostUsd),
            TokenPresentation.Format(breakdown.OutputTokens, LocalizationManager.CurrentLanguageCode), Cost(breakdown.OutputCostUsd),
            TokenPresentation.Format(breakdown.ReasoningTokens, LocalizationManager.CurrentLanguageCode), Cost(breakdown.ReasoningCostUsd),
            TokenPresentation.Format(breakdown.TotalTokens, LocalizationManager.CurrentLanguageCode), Cost(breakdown.TotalCostUsd), note,
            Fraction(breakdown.CachedReadTokens, breakdown.TotalTokens), Fraction(breakdown.InputTokens, breakdown.TotalTokens),
            Fraction(breakdown.OutputTokens, breakdown.TotalTokens), Fraction(breakdown.ReasoningTokens, breakdown.TotalTokens), Fraction(breakdown.TotalTokens, breakdown.TotalTokens));
    }

    private void UpdateVisibleChatProjects() => HasVisibleChatProjects = ChatProjects.Any(project => project.IsVisible);

    private static double Fraction(long value, long total) => total <= 0 ? 0 : Math.Min(1, Math.Max(0, value / (double)total));

    private static string DisplayProject(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return LocalizationManager.Text("UnknownProject");
        var trimmed = projectPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? LocalizationManager.Text("UnknownProject") : name;
    }

    private static string ProjectKey(string? projectPath) => string.IsNullOrWhiteSpace(projectPath)
        ? ""
        : projectPath!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ApplyForecast(WeeklyForecast forecast)
    {
        _lastForecast = forecast;
        IsExhaustionRisk = forecast.Status == WeeklyForecastCalculator.RiskStatus;
        Forecast = FormatForecast(forecast);
    }
    private string FormatForecast(WeeklyForecast forecast)
    {
        var status = LocalizationManager.TranslateKnown(forecast.Status);
        if (forecast.ProjectedPercent is not double projected) return status;
        var text = LocalizationManager.Format("ProjectedForecast", status, WeeklyForecastCalculator.FormatProjectedPercent(projected, LocalizationManager.CurrentLanguageCode));
        return forecast.Status == WeeklyForecastCalculator.RiskStatus && forecast.ExhaustsAt is { } exhausts
            ? text + " · " + LocalizationManager.Format("ExhaustsAround", WeeklyForecastCalculator.FormatExhaustsAt(exhausts, _clock(), LocalizationManager.CurrentLanguageCode))
            : text;
    }
    private void UpdateActiveQuotaCycle(QuotaWindow? weekly)
    {
        _activeQuotaCycle = weekly?.ResetsAt is { } end && weekly.WindowDurationMins is > 0
            ? (end.AddMinutes(-weekly.WindowDurationMins.Value), end)
            : null;
        RefreshRanking();
    }
    private void RefreshFormattedCosts()
    {
        if (!_hasAnalytics) return;
        WeeklyCost = _lastWeeklyEstimate is { } estimate ? CurrencyPresentation.FormatCost(estimate.CostUsd, estimate.CostBrl, CurrencyCode, LocalizationManager.CurrentLanguageCode) : "--";
        TodayCost = CurrencyPresentation.FormatCost(_lastTodayUsd, _lastTodayBrl, CurrencyCode, LocalizationManager.CurrentLanguageCode);
        MonthCost = CurrencyPresentation.FormatCost(_lastMonthUsd, _lastMonthBrl, CurrencyCode, LocalizationManager.CurrentLanguageCode);
        Cost = MonthCost;
    }
    public void RefreshLocalization()
    {
        if (_hasQuotaSnapshot)
            ApplyWeeklyDisplay(_lastWeekly);
        else
            Reset = LocalizationManager.Text("LoadingWeeklyQuota");
        if (_lastAnalytics is { } analytics)
        {
            Today = TokenPresentation.Format(analytics.TodayTokens, LocalizationManager.CurrentLanguageCode);
            Month = TokenPresentation.Format(analytics.MonthTokens, LocalizationManager.CurrentLanguageCode);
            WeeklyTokens = WeeklyTokenCount is { } weeklyTokens ? TokenPresentation.Format(weeklyTokens, LocalizationManager.CurrentLanguageCode) : "--";
        }
        if (_lastForecast is { } forecast) ApplyForecast(forecast);
        else Forecast = LocalizationManager.Text("InsufficientData");
        Status = LocalizationManager.TranslateKnown(Status);
        NotifyProfileProperties();
        PropertyChanged?.Invoke(this, new(nameof(UpdateAvailableMessage)));
        foreach (var row in ActiveAgents) row.RefreshLocalization();
        foreach (var row in AgentItems.Where(row => row.IsCompleted)) row.RefreshLocalization();
        PropertyChanged?.Invoke(this, new(nameof(AgentIndicatorTooltip)));
        RefreshRanking();
        RefreshChatDetails();
        RefreshFormattedCosts();
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "") { if (!EqualityComparer<T>.Default.Equals(field, value)) { field = value; PropertyChanged?.Invoke(this, new(name)); } }

    private enum RankingPeriod { Day, Week, Month }
}
