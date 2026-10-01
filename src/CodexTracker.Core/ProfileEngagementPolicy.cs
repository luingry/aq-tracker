namespace CodexTracker.Core;

public enum AgentProvider { Codex, Claude }

public readonly record struct ProfileActivity(bool IsForeground, bool IsMinimized, bool HasActiveWork, bool HasUnreadWork)
{
    public bool IsEngaged => IsForeground && !IsMinimized || HasActiveWork || HasUnreadWork;
}

public sealed record ProfileGauge(AgentProvider Provider, string Window, QuotaWindow? Quota);

/// <summary>Presentation policy independent of WPF, process discovery and authentication.</summary>
public static class ProfileEngagementPolicy
{
    public static IReadOnlyList<ProfileGauge> Select(ProfileActivity codex, ProfileActivity claude,
        AgentProvider lastProfile, string display, IEnumerable<QuotaWindow> codexWindows, IEnumerable<QuotaWindow> claudeWindows)
    {
        if (codex.IsEngaged && claude.IsEngaged)
            return new[] { Restrictive(AgentProvider.Codex, display, codexWindows), Restrictive(AgentProvider.Claude, display, claudeWindows) };
        var provider = claude.IsEngaged ? AgentProvider.Claude : codex.IsEngaged ? AgentProvider.Codex : lastProfile;
        var windows = provider == AgentProvider.Claude ? claudeWindows : codexWindows;
        var result = new List<ProfileGauge>();
        var five = Find(provider, windows, "5h");
        if (display != "7d" && five is not null) result.Add(new(provider, "5h", five));
        if (display != "5h") result.Add(new(provider, "7d", Find(provider, windows, "7d")));
        // An unconnected profile must still have a visible unknown indicator.
        if (result.Count == 0 && provider == AgentProvider.Claude) result.Add(new(provider, display == "5h" ? "5h" : "7d", null));
        return result;
    }

    private static ProfileGauge Restrictive(AgentProvider provider, string display, IEnumerable<QuotaWindow> windows)
    {
        var candidates = new[] { new ProfileGauge(provider, "5h", Find(provider, windows, "5h")), new ProfileGauge(provider, "7d", Find(provider, windows, "7d")) }
            .Where(gauge => display == "both" || gauge.Window == display).ToArray();
        return candidates.Where(gauge => gauge.Quota is { HasUsage: true }).OrderBy(gauge => gauge.Quota!.RemainingPercent).FirstOrDefault()
            ?? candidates.First();
    }

    public static QuotaWindow? Find(AgentProvider provider, IEnumerable<QuotaWindow> windows, string window) => provider == AgentProvider.Codex
        ? window == "5h" ? OfficialCodexQuotaWindows.FiveHours(windows) : OfficialCodexQuotaWindows.Weekly(windows)
        : windows.FirstOrDefault(item => item.Id == (window == "5h" ? "claude:five_hour" : "claude:seven_day"));
}
