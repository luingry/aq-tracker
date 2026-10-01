namespace AqTracker.Core;

public static class ResetCountdown
{
    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now) => Format(resetsAt, now, "pt-BR");

    public static string Format(DateTimeOffset? resetsAt, DateTimeOffset now, string? languageCode)
    {
        var isEnglish = string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase);
        if (resetsAt is null) return isEnglish ? "reset unavailable" : "reset indisponível";
        var span = resetsAt.Value - now;
        if (span <= TimeSpan.Zero) return isEnglish ? "resetting now" : "reiniciando agora";
        var prefix = isEnglish ? "resets in" : "reinicia em";
        var countdown = span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1
                ? $"{span.Hours}h {span.Minutes}m"
                : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
        var localReset = resetsAt.Value.ToLocalTime();
        var absoluteReset = isEnglish
            ? localReset.ToString("MM/dd 'at' h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US"))
            : localReset.ToString("dd/MM 'as' HH:mm'h'", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        return $"{prefix} {countdown} ({absoluteReset})";
    }
}
