namespace AqTracker.Core;

public sealed record WeeklyForecast(string Status, double? ProjectedPercent, DateTimeOffset? ExhaustsAt);

/// <summary>
/// Projects the usage of a quota window at its reset. The baseline is the cycle-average pace (used / elapsed), the
/// same pace model used by popular trackers such as CodexBar; it is stable but slow to notice a change of behavior.
/// When the app observed the window for a full recent lookback, the recent rate is blended in with equal weight, so a
/// sprint (or a pause) shows up within the hour instead of only after days. Early in a cycle a handful of minutes says
/// little about the rest of the window, so no projection is offered until enough of it elapsed, unless usage is
/// already high enough to matter.
/// </summary>
public static class WeeklyForecastCalculator
{
    private const string Insufficient = "Dados insuficientes";
    public const string RiskStatus = "Risco de esgotar antes do reset";
    private const double MinimumObservedFraction = 0.05;
    private const double SignificantUsedPercent = 20;
    private const double RecentWeight = 0.5;
    private const double MaximumAnchorSpanFactor = 3;

    /// <summary>Recent-rate lookback: a full day for weekly windows (day/night neutral), a fifth of shorter windows.</summary>
    public static TimeSpan Lookback(int windowDurationMins) => windowDurationMins >= OfficialCodexQuotaWindows.WeeklyMinimumMinutes
        ? TimeSpan.FromHours(24)
        : TimeSpan.FromMinutes(Math.Max(1, windowDurationMins / 5.0));

    public static WeeklyForecast Calculate(QuotaWindow? window, DateTimeOffset asOf, IReadOnlyList<TimedQuotaUsage>? recent = null)
    {
        if (window?.ResetsAt is null || window.WindowDurationMins is not > 0 || !window.HasUsage ||
            !Net48Compatibility.IsFinite(window.UsedPercent) || window.UsedPercent is <= 0 or > 100)
            return new(Insufficient, null, null);

        var duration = TimeSpan.FromMinutes(window.WindowDurationMins.Value);
        DateTimeOffset start;
        try { start = window.ResetsAt.Value.Subtract(duration); }
        catch (ArgumentOutOfRangeException) { return new(Insufficient, null, null); }

        var elapsed = asOf - start;
        if (elapsed <= TimeSpan.FromMinutes(1) || elapsed >= duration || asOf >= window.ResetsAt.Value)
            return new(Insufficient, null, null);

        var used = window.UsedPercent;
        var remaining = window.ResetsAt.Value - asOf;
        var averageRate = used / elapsed.TotalSeconds;
        if (used >= 100) return new("Limite esgotado", used + averageRate * remaining.TotalSeconds, asOf);
        if (elapsed.TotalSeconds < duration.TotalSeconds * MinimumObservedFraction && used < SignificantUsedPercent)
            return new(Insufficient, null, null);

        var rate = RecentRate(recent, used, start, asOf, Lookback(window.WindowDurationMins.Value)) is double recentRate
            ? (1 - RecentWeight) * averageRate + RecentWeight * recentRate
            : averageRate;
        var projected = used + rate * remaining.TotalSeconds;
        if (!Net48Compatibility.IsFinite(projected)) return new(Insufficient, null, null);
        if (Math.Round(projected, 1, MidpointRounding.AwayFromZero) <= 100) return new("Deve durar até o reset", projected, null);
        var exhausts = asOf.AddSeconds(Math.Min(remaining.TotalSeconds, (100 - used) / rate));
        return new(RiskStatus, projected, exhausts);
    }

    /// <summary>
    /// Percent per second between the newest sample at least one lookback old and now. Samples outside the current cycle,
    /// above the current reading (a different cycle) or too old to describe "recent" are ignored.
    /// </summary>
    private static double? RecentRate(IReadOnlyList<TimedQuotaUsage>? samples, double used, DateTimeOffset start, DateTimeOffset asOf, TimeSpan lookback)
    {
        if (samples is null || samples.Count == 0) return null;
        TimedQuotaUsage? anchor = null;
        var latestAnchorAt = asOf - lookback;
        foreach (var sample in samples)
            if (sample.At >= start && sample.At <= latestAnchorAt && Net48Compatibility.IsFinite(sample.UsedPercent) &&
                sample.UsedPercent <= used + 0.5 && (anchor is null || sample.At > anchor.At))
                anchor = sample;
        if (anchor is null) return null;
        var span = asOf - anchor.At;
        if (span.TotalSeconds > lookback.TotalSeconds * MaximumAnchorSpanFactor) return null;
        return Math.Max(0, used - anchor.UsedPercent) / span.TotalSeconds;
    }

    public static string FormatProjectedPercent(double projected) => FormatProjectedPercent(projected, "pt-BR");

    public static string FormatProjectedPercent(double projected, string? languageCode)
    {
        var culture = string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.CultureInfo.GetCultureInfo("en-US")
            : System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return projected < 101 ? projected.ToString("0.0", culture) + "%" : projected.ToString("0", culture) + "%";
    }

    /// <summary>Local exhaustion time: only the clock time on the same day, otherwise date and time.</summary>
    public static string FormatExhaustsAt(DateTimeOffset exhaustsAt, DateTimeOffset now, string? languageCode)
    {
        var local = exhaustsAt.ToLocalTime();
        var english = string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase);
        var culture = System.Globalization.CultureInfo.GetCultureInfo(english ? "en-US" : "pt-BR");
        var sameDay = local.Date == now.ToLocalTime().Date;
        return local.ToString(english ? sameDay ? "h:mm tt" : "MM/dd h:mm tt" : sameDay ? "HH:mm" : "dd/MM HH:mm", culture);
    }
}

/// <summary>
/// In-memory, bounded record of recent readings per quota window, used only for the recent-rate part of the forecast.
/// Unchanged readings are kept at a throttled interval so a lookback anchor exists after idle periods; a new cycle
/// (different reset or a drop in usage) restarts the series. Nothing is written to disk.
/// </summary>
public sealed class QuotaRateHistory
{
    private static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);

    private sealed class Series(DateTimeOffset resetsAt)
    {
        public DateTimeOffset ResetsAt = resetsAt;
        public readonly List<TimedQuotaUsage> Samples = [];
    }

    public void Record(IEnumerable<QuotaWindow> windows, DateTimeOffset at)
    {
        foreach (var window in windows) Record(window, at);
    }

    public void Record(QuotaWindow window, DateTimeOffset at)
    {
        if (!window.HasUsage || window.ResetsAt is not { } reset || window.WindowDurationMins is not > 0 || !Net48Compatibility.IsFinite(window.UsedPercent)) return;
        var lookback = WeeklyForecastCalculator.Lookback(window.WindowDurationMins.Value);
        if (!_series.TryGetValue(window.Id, out var series)) _series[window.Id] = series = new(reset);
        var samples = series.Samples;
        var last = samples.Count > 0 ? samples[samples.Count - 1] : null;
        if (Math.Abs((series.ResetsAt - reset).TotalMinutes) > ResetTolerance.TotalMinutes || last is not null && window.UsedPercent < last.UsedPercent - 1)
        {
            samples.Clear(); series.ResetsAt = reset; last = null;
        }
        if (last is not null && (at <= last.At || window.UsedPercent == last.UsedPercent && at - last.At < TimeSpan.FromTicks(lookback.Ticks / 24))) return;
        samples.Add(new(at, window.UsedPercent));
        var cutoff = at - TimeSpan.FromTicks(lookback.Ticks * 3);
        var expired = 0;
        while (expired < samples.Count - 1 && samples[expired].At < cutoff) expired++;
        if (expired > 0) samples.RemoveRange(0, expired);
    }

    public IReadOnlyList<TimedQuotaUsage> For(QuotaWindow? window) =>
        window is not null && _series.TryGetValue(window.Id, out var series) && window.ResetsAt is { } reset &&
        Math.Abs((series.ResetsAt - reset).TotalMinutes) <= ResetTolerance.TotalMinutes ? series.Samples : [];
}
