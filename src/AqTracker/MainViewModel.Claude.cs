using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using AqTracker.Core;

namespace AqTracker;

public sealed record CompactQuotaRow(string Label, string Percent, double Remaining, Brush Brush,
    bool IsWorking, string Tooltip, string AccessibleName);

public sealed partial class MainViewModel
{
    private IReadOnlyList<QuotaWindow> _codexWindows = [], _claudeWindows = [];
    private IReadOnlyList<ProfileGauge> _profileGauges = [new(AgentProvider.Codex, "7d", null)];
    private ProfileActivity _codexActivity, _claudeActivity;
    private AgentProvider _lastProfile = AgentProvider.Codex;
    private ClaudeConnectionState _claudeState;
    private bool _claudeStale = true;
    private bool _claudeEnabled = true;
    public bool ClaudeProfileEnabled => _claudeEnabled;
    public IReadOnlyList<CompactQuotaRow> CompactQuotas => _profileGauges.Select(gauge => new CompactQuotaRow(
        gauge.Window, QuotaPresentation.FormatWeeklyRemaining(gauge.Quota), gauge.Quota?.RemainingPercent ?? 0,
        ProfileBrush(gauge.Provider), ProfileWorking(gauge.Provider), GaugeTooltip(gauge),
        LocalizationManager.Format("ProfileQuotaIndicator", gauge.Provider, gauge.Window))).ToArray();
    // The tray is an always-available overview, independent of foreground/work activity.
    public IReadOnlyList<CompactQuotaRow> NotificationQuotas
    {
        get
        {
            var rows = new List<CompactQuotaRow>();
            foreach (var provider in new[] { AgentProvider.Codex, AgentProvider.Claude })
            {
                if (provider == AgentProvider.Claude && !_claudeEnabled) continue;
                foreach (var period in new[] { "5h", "7d" })
                {
                    if (_compactQuotaDisplay != "both" && _compactQuotaDisplay != period) continue;
                    var quota = ProfileEngagementPolicy.Find(provider,
                        provider == AgentProvider.Codex ? _codexWindows : _claudeWindows, period);
                    if (quota is null || !quota.HasUsage) continue;
                    var gauge = new ProfileGauge(provider, period, quota);
                    rows.Add(new(period, QuotaPresentation.FormatWeeklyRemaining(quota), quota.RemainingPercent,
                        ProfileBrush(provider), false, GaugeTooltip(gauge),
                        LocalizationManager.Format("ProfileQuotaIndicator", provider, period)));
                }
            }
            return rows;
        }
    }

    public void SetClaudeEnabled(bool enabled)
    {
        _claudeEnabled = enabled;
        if (!enabled) { _lastProfile = AgentProvider.Codex; _claudeActivity = default; }
        PropertyChanged?.Invoke(this, new(nameof(ClaudeProfileEnabled)));
        RefreshProfilePresentation();
    }
    private string _profileTheme = "Escuro", _codexAccent = AccentPalette.DefaultBaseHex, _claudeAccent = "#D97757";

    private ProfileGauge LeftGauge => _profileGauges.FirstOrDefault() ?? new(AgentProvider.Codex, "5h", null);
    private ProfileGauge RightGauge => _profileGauges.LastOrDefault() ?? new(AgentProvider.Codex, "7d", null);
    public string CompactLeftLabel => LeftGauge.Window;
    public string CompactRightLabel => RightGauge.Window;
    public string CompactLeftAccessibleName => LocalizationManager.Format("ProfileQuotaIndicator", LeftGauge.Provider, LeftGauge.Window);
    public string CompactRightAccessibleName => LocalizationManager.Format("ProfileQuotaIndicator", RightGauge.Provider, RightGauge.Window);
    public string CompactLeftTooltip => GaugeTooltip(LeftGauge);
    public string CompactRightTooltip => GaugeTooltip(RightGauge);
    public string CompactLeftPercent => QuotaPresentation.FormatWeeklyRemaining(LeftGauge.Quota);
    public string CompactRightPercent => QuotaPresentation.FormatWeeklyRemaining(RightGauge.Quota);
    public double CompactLeftRemaining => LeftGauge.Quota?.RemainingPercent ?? 0;
    public double CompactRightRemaining => RightGauge.Quota?.RemainingPercent ?? 0;
    public Brush CompactLeftBrush => ProfileBrush(LeftGauge.Provider);
    public Brush CompactRightBrush => ProfileBrush(RightGauge.Provider);
    public Brush ClaudeBrush => ProfileBrush(AgentProvider.Claude);
    public bool CompactLeftWorking => ProfileWorking(LeftGauge.Provider);
    public bool CompactRightWorking => ProfileWorking(RightGauge.Provider);
    public bool IsCodexWorkAnimationEnabled => ProfileWorking(AgentProvider.Codex);
    public bool IsClaudeWorkAnimationEnabled => ProfileWorking(AgentProvider.Claude);
    public string ClaudeStatus => LocalizationManager.Text(_claudeState == ClaudeConnectionState.Connected ? "ClaudeConnected" :
        _claudeState == ClaudeConnectionState.Reconnect ? "ClaudeNeedsReconnect" : "ClaudeDisconnected");
    public bool IsClaudeConnected => _claudeState == ClaudeConnectionState.Connected;
    public bool IsClaudeReconnectRequired => _claudeState == ClaudeConnectionState.Reconnect;
    public string ClaudeConnectionHint => LocalizationManager.Text(IsClaudeConnected ? "ClaudeConnectedHint" : "ClaudeSignInHint");
    public string ClaudeConnectLabel =>LocalizationManager.Text(IsClaudeReconnectRequired ? "ClaudeReconnect" : "ClaudeConnect");

    // Settings shows exactly one action set for the current connection state: connect actions
    // while signed out, disconnect while signed in, and only cancel while a sign-in is pending.
    private bool _isClaudeLoginBusy, _isClaudeManualEntryOpen;
    public bool IsClaudeLoginBusy { get => _isClaudeLoginBusy; set { _isClaudeLoginBusy = value; NotifyClaudeActions(); } }
    public bool IsClaudeManualEntryOpen { get => _isClaudeManualEntryOpen; set { _isClaudeManualEntryOpen = value; NotifyClaudeActions(); } }
    private bool IsClaudeSignInPending => _isClaudeLoginBusy || _isClaudeManualEntryOpen;
    public bool ShowClaudeConnectActions => !IsClaudeSignInPending && _claudeState != ClaudeConnectionState.Connected;
    public bool ShowClaudeDisconnect => !IsClaudeSignInPending && _claudeState != ClaudeConnectionState.Disconnected;
    public bool ShowClaudeCancel => IsClaudeSignInPending;
    private void NotifyClaudeActions()
    {
        foreach (var name in new[] { nameof(IsClaudeLoginBusy), nameof(IsClaudeManualEntryOpen), nameof(ShowClaudeConnectActions), nameof(ShowClaudeDisconnect), nameof(ShowClaudeCancel) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public string ClaudeDetailStatus => _claudeState != ClaudeConnectionState.Connected ? LocalizationManager.Text("ClaudeConnectSettings") :
        LocalizationManager.Text(_claudeStale ? "ClaudeStale" : "ClaudeConnected");
    public string ClaudeFiveHour => QuotaPresentation.FormatWeeklyRemaining(ProfileEngagementPolicy.Find(AgentProvider.Claude, _claudeWindows, "5h"));
    public string ClaudeWeekly => QuotaPresentation.FormatWeeklyRemaining(ProfileEngagementPolicy.Find(AgentProvider.Claude, _claudeWindows, "7d"));
    public double ClaudeFiveHourRemainingPercent => ProfileEngagementPolicy.Find(AgentProvider.Claude, _claudeWindows, "5h")?.RemainingPercent ?? 0;
    public double ClaudeWeeklyRemainingPercent => ProfileEngagementPolicy.Find(AgentProvider.Claude, _claudeWindows, "7d")?.RemainingPercent ?? 0;
    public string ClaudeFiveHourReset => ClaudeReset("5h");
    public string ClaudeWeeklyReset => ClaudeReset("7d");
    public string ClaudeFiveHourAccessibleName => LocalizationManager.Format("ProfileQuotaIndicator", AgentProvider.Claude, "5h");
    public string ClaudeWeeklyAccessibleName => LocalizationManager.Format("ProfileQuotaIndicator", AgentProvider.Claude, "7d");

    public void SetProfileColors(string theme, string codex, string claude)
    {
        _profileTheme = theme; _codexAccent = codex; _claudeAccent = claude;
        NotifyProfileProperties();
    }
    public void SetProfileActivity(ProfileActivity codex, ProfileActivity claude)
    {
        if (_codexActivity == codex && _claudeActivity == claude) return;
        _codexActivity = codex; _claudeActivity = claude;
        if (codex.IsEngaged) _lastProfile = AgentProvider.Codex;
        else if (claude.IsEngaged) _lastProfile = AgentProvider.Claude;
        RefreshProfilePresentation();
    }
    public void ApplyClaude(RateLimitSnapshot? snapshot, ClaudeConnectionState state, bool stale)
    {
        _claudeWindows = snapshot?.Windows ?? [];
        _claudeState = state; _claudeStale = stale;
        RefreshProfilePresentation();
    }
    private Brush ProfileBrush(AgentProvider provider)
    {
        var palette = AccentPalette.Create(provider == AgentProvider.Claude ? _claudeAccent : _codexAccent, _profileTheme == "Escuro");
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.AccentHex));
        brush.Freeze();
        return brush;
    }
    private bool ProfileWorking(AgentProvider provider) => (provider == AgentProvider.Claude ? _claudeActivity : _codexActivity).HasActiveWork && System.Windows.SystemParameters.ClientAreaAnimation;
    private string GaugeTooltip(ProfileGauge gauge) => LocalizationManager.Format("ProfileQuotaIndicator", gauge.Provider, gauge.Window) +
        " · " + QuotaPresentation.FormatWeeklyRemaining(gauge.Quota) + "\n" +
        (gauge.Provider == AgentProvider.Claude ? ClaudeDetailStatus + "\n" : "") +
        ResetCountdown.Format(gauge.Quota?.ResetsAt, _clock(), LocalizationManager.CurrentLanguageCode);
    private string ClaudeReset(string window) => ResetCountdown.Format(ProfileEngagementPolicy.Find(AgentProvider.Claude, _claudeWindows, window)?.ResetsAt, _clock(), LocalizationManager.CurrentLanguageCode);
    private void RefreshProfilePresentation()
    {
        _profileGauges = ProfileEngagementPolicy.Select(_codexActivity, _claudeActivity, _lastProfile, _compactQuotaDisplay, _codexWindows, _claudeWindows);
        NotifyProfileProperties();
    }
    private void NotifyProfileProperties()
    {
        foreach (var name in new[] { nameof(NotificationQuotas), nameof(CompactQuotas), nameof(ShowCompactFiveHour), nameof(ShowCompactWeekly), nameof(CompactQuotaCount),
            nameof(CompactLeftLabel), nameof(CompactRightLabel), nameof(CompactLeftPercent), nameof(CompactRightPercent),
            nameof(CompactLeftAccessibleName), nameof(CompactRightAccessibleName),
            nameof(CompactLeftTooltip), nameof(CompactRightTooltip),
            nameof(CompactLeftRemaining), nameof(CompactRightRemaining), nameof(CompactLeftBrush), nameof(CompactRightBrush),
            nameof(CompactLeftWorking), nameof(CompactRightWorking), nameof(IsCodexWorkAnimationEnabled), nameof(IsClaudeWorkAnimationEnabled), nameof(ClaudeBrush), nameof(ClaudeStatus), nameof(ClaudeDetailStatus),
            nameof(ClaudeFiveHour), nameof(ClaudeWeekly), nameof(ClaudeFiveHourReset), nameof(ClaudeWeeklyReset),
            nameof(ClaudeFiveHourRemainingPercent), nameof(ClaudeWeeklyRemainingPercent),
            nameof(ClaudeFiveHourAccessibleName), nameof(ClaudeWeeklyAccessibleName),
            nameof(IsClaudeConnected), nameof(IsClaudeReconnectRequired), nameof(ClaudeConnectLabel), nameof(ClaudeConnectionHint) })
            PropertyChanged?.Invoke(this, new(name));
        NotifyClaudeActions();
    }
}
