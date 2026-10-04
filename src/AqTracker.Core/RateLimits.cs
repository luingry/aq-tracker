using System.Globalization;
using System.Text.Json;

namespace AqTracker.Core;

public enum ConnectionState { Loading, Live, Stale, SignedOut, Error }

public sealed record QuotaWindow(string Id, string Label, double UsedPercent, DateTimeOffset? ResetsAt, int? WindowDurationMins, string? Detail = null, bool HasUsage = true, bool Expired = false)
{
    public double RemainingPercent => HasUsage ? Net48Compatibility.Clamp(100 - UsedPercent, 0, 100) : 0;
}

public sealed record RateLimitSnapshot(
    IReadOnlyList<QuotaWindow> Windows,
    string? PlanType,
    string? Credits,
    string? ResetCredits,
    DateTimeOffset ReceivedAt,
    bool IsSparse = false);

public static class OfficialCodexQuotaWindows
{
    public const int FiveHourMinimumMinutes = 240;
    public const int FiveHourMaximumMinutes = 360;
    public const int WeeklyMinimumMinutes = 10000;

    public static QuotaWindow? FiveHours(IEnumerable<QuotaWindow> windows) => windows
        .Where(IsOfficialCodex)
        .Where(window => window.WindowDurationMins is >= FiveHourMinimumMinutes and <= FiveHourMaximumMinutes)
        .OrderBy(window => window.WindowDurationMins)
        .FirstOrDefault();

    public static QuotaWindow? Weekly(IEnumerable<QuotaWindow> windows) => windows
        .Where(IsOfficialCodex)
        .Where(window => window.WindowDurationMins is >= WeeklyMinimumMinutes)
        .OrderBy(window => window.WindowDurationMins)
        .FirstOrDefault();

    private static bool IsOfficialCodex(QuotaWindow window) => window.Id.StartsWith("codex:", StringComparison.Ordinal);
}

public static class RateLimitParser
{
    public static RateLimitSnapshot Parse(JsonElement result, DateTimeOffset receivedAt)
    {
        var windows = new List<QuotaWindow>();
        var rateLimits = GetObject(result, "rateLimits");
        var plan = rateLimits is { } limits ? FindString(limits, "planType") : null;
        var credits = rateLimits is { } snapshotLimits ? FormatCredits(GetObject(snapshotLimits, "credits")) : null;
        var resetCredits = FormatResetCredits(GetObject(result, "rateLimitResetCredits"));

        if (rateLimits is { } primary) CollectNamedWindows(primary, windows, "codex", "Codex", true);
        if (result.TryGetProperty("rateLimitsByLimitId", out var byLimit) && byLimit.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in byLimit.EnumerateObject())
            {
                // The canonical codex bucket normally duplicates rateLimits, but is the authoritative
                // fallback when the legacy envelope is absent. Keep envelope fields when both exist.
                if (bucket.Name.Equals("codex", StringComparison.OrdinalIgnoreCase))
                {
                    var fallback = new List<QuotaWindow>();
                    CollectNamedWindows(bucket.Value, fallback, "codex", "Codex", false);
                    foreach (var window in fallback)
                        if (!windows.Any(existing => string.Equals(existing.Id, window.Id, StringComparison.Ordinal))) windows.Add(window);
                    continue;
                }
                var bucketName = FindString(bucket.Value, "limitName", "name") ?? Humanize(bucket.Name);
                CollectNamedWindows(bucket.Value, windows, bucket.Name, bucketName, false);
            }
        }
        return new RateLimitSnapshot(windows, plan, credits, resetCredits, receivedAt);
    }

    public static RateLimitSnapshot Merge(RateLimitSnapshot current, RateLimitSnapshot update)
    {
        var byId = current.Windows.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var item in update.Windows)
        {
            byId[item.Id] = byId.TryGetValue(item.Id, out var previous) && IsSameWindow(previous, item, update.ReceivedAt)
                ? item with
                {
                    Label = item.WindowDurationMins is null ? previous.Label : item.Label,
                    ResetsAt = item.ResetsAt ?? previous.ResetsAt,
                    WindowDurationMins = item.WindowDurationMins ?? previous.WindowDurationMins
                }
                : item;
        }
        return current with
        {
            Windows = byId.Values.OrderBy(x => x.Id.StartsWith("codex:", StringComparison.Ordinal) ? 0 : 1).ThenBy(x => x.WindowDurationMins ?? int.MaxValue).ToArray(),
            PlanType = update.PlanType ?? current.PlanType,
            Credits = update.Credits ?? current.Credits,
            ResetCredits = update.ResetCredits ?? current.ResetCredits,
            ReceivedAt = update.ReceivedAt,
            IsSparse = false
        };
    }

    private static bool IsSameWindow(QuotaWindow previous, QuotaWindow update, DateTimeOffset receivedAt) =>
        previous.ResetsAt is { } reset && reset > receivedAt &&
        previous.WindowDurationMins is > 0 &&
        Net48Compatibility.IsFinite(previous.UsedPercent) && Net48Compatibility.IsFinite(update.UsedPercent) &&
        update.UsedPercent >= previous.UsedPercent &&
        (update.ResetsAt is null || update.ResetsAt == reset) &&
        (update.WindowDurationMins is null || update.WindowDurationMins == previous.WindowDurationMins);

    private static void CollectNamedWindows(JsonElement node, List<QuotaWindow> output, string limitId, string limitName, bool isRoot)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var candidates = new[] { "primary", "secondary", "individualLimit" };
        foreach (var name in candidates)
        {
            if (!node.TryGetProperty(name, out var window)) continue;
            if (!TryReadWindow(window, $"{limitId}:{name}", limitName, out var parsed)) continue;
            output.Add(parsed);
        }
        // Sparse protocol messages may only contain a window shape at the current object level.
        if (!isRoot && TryReadWindow(node, limitId + ":primary", limitName, out var direct)) output.Add(direct);
    }

    private static bool TryReadWindow(JsonElement node, string id, string parentName, out QuotaWindow window)
    {
        window = default!;
        if (node.ValueKind != JsonValueKind.Object) return false;
        if (!TryGetNumber(node, "usedPercent", out var used)) return false;
        var duration = TryGetInt(node, "windowDurationMins");
        var kind = id.Substring(id.LastIndexOf(':') + 1);
        var suffix = duration is >= 240 and <= 360 ? "5-hour limit" : duration >= 10000 ? "Weekly limit" : FormatWindow(duration);
        var label = parentName == "Codex" ? suffix : parentName + " - " + suffix;
        if (!Net48Compatibility.IsFinite(used)) return false;
        window = new QuotaWindow(id, label, Net48Compatibility.Clamp(used, 0, 100), TryGetDate(node, "resetsAt"), duration, FindString(node, "reachedType"));
        return true;
    }

    private static JsonElement? GetObject(JsonElement node, string name) => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
    private static string FormatWindow(int? mins) => mins is null ? "Usage limit" : mins >= 60 ? $"{mins / 60}-hour limit" : $"{mins}-minute limit";
    private static string Humanize(string value) => value.Replace('_', ' ').Replace('-', ' ');
    private static string? FindString(JsonElement node, params string[] names) => names.Select(n => node.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static bool TryGetNumber(JsonElement node, string name, out double value) { value = 0; return node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out value); }
    private static int? TryGetInt(JsonElement node, string name) => node.TryGetProperty(name, out var v) && v.TryGetInt32(out var value) ? value : null;
    private static DateTimeOffset? TryGetDate(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
    private static string? FormatCredits(JsonElement? credits)
    {
        if (credits is not { } value) return null;
        if (value.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind == JsonValueKind.True) return "Credits: unlimited";
        if (value.TryGetProperty("hasCredits", out var has) && has.ValueKind == JsonValueKind.False) return "Credits: unavailable";
        if (value.TryGetProperty("balance", out var balance) && TryGetDecimal(balance, out var amount)) return "Credits: " + amount.ToString("0.##", CultureInfo.InvariantCulture);
        return "Credits: available";
    }
    private static bool TryGetDecimal(JsonElement value, out decimal amount)
    {
        amount = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out amount);
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
    private static string? FormatResetCredits(JsonElement? credits)
    {
        if (credits is not { } value) return null;
        if (value.TryGetProperty("availableCount", out var count) && count.TryGetInt32(out var available)) return $"Reset credits: {available}";
        return null;
    }
}
