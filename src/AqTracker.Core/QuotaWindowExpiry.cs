namespace AqTracker.Core;

/// <summary>
/// A snapshot keeps its last reported percentage until a newer one arrives. When the reset time has long
/// passed and no fresh data replaced it (backoff, offline provider, server still reporting the old cycle),
/// that percentage belongs to a cycle that no longer exists and must not be shown as current.
/// </summary>
public static class QuotaWindowExpiry
{
    /// <summary>Time allowed after the reset for the next poll to deliver the new cycle.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public static bool IsExpired(QuotaWindow window, DateTimeOffset now) =>
        window.HasUsage && window.ResetsAt is { } reset && now - reset >= Grace;

    /// <summary>
    /// Returns windows whose cycle ended as unknown: the old percentage no longer describes anything and the usage of the
    /// new cycle has not been reported yet, so it is never presented as a made-up value (neither the stale one nor 100%).
    /// </summary>
    public static IReadOnlyList<QuotaWindow> Normalize(IEnumerable<QuotaWindow> windows, DateTimeOffset now) => windows
        .Select(window => IsExpired(window, now) ? window with { HasUsage = false, ResetsAt = null, Detail = null, Expired = true } : window)
        .ToArray();

    public static QuotaWindow? Normalize(QuotaWindow? window, DateTimeOffset now) =>
        window is not null && IsExpired(window, now) ? Normalize(new[] { window }, now)[0] : window;

    public static int CountExpired(IEnumerable<QuotaWindow> windows, DateTimeOffset now) => windows.Count(window => IsExpired(window, now));
}
