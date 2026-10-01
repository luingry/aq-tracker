namespace AqTracker.Core;

public sealed record WeeklyForecast(string Status, double? ProjectedPercent, DateTimeOffset? ExhaustsAt);
public static class WeeklyForecastCalculator
{
    private const string Insufficient = "Dados insuficientes";

    public static WeeklyForecast Calculate(QuotaWindow? window, DateTimeOffset asOf)
    {
        if (window?.ResetsAt is null || window.WindowDurationMins is not > 0 ||
            !Net48Compatibility.IsFinite(window.UsedPercent) || window.UsedPercent is <= 0 or > 100)
            return new(Insufficient, null, null);

        var duration = TimeSpan.FromMinutes(window.WindowDurationMins.Value);
        DateTimeOffset start;
        try { start = window.ResetsAt.Value.Subtract(duration); }
        catch (ArgumentOutOfRangeException) { return new(Insufficient, null, null); }

        var elapsed = asOf - start;
        if (elapsed <= TimeSpan.FromMinutes(1) || elapsed >= duration || asOf >= window.ResetsAt.Value)
            return new(Insufficient, null, null);

        var fraction = elapsed.TotalSeconds / duration.TotalSeconds;
        var projected = window.UsedPercent / fraction;
        if (!Net48Compatibility.IsFinite(projected)) return new(Insufficient, null, null);
        if (window.UsedPercent >= 100) return new("Limite esgotado", projected, asOf);
        if (Math.Round(projected, 1, MidpointRounding.AwayFromZero) <= 100) return new("Deve durar até o reset", projected, null);
        var exhausts = start.AddSeconds(elapsed.TotalSeconds * (100 / window.UsedPercent));
        return new("Risco de esgotar antes do reset", projected, exhausts);
    }

    public static string FormatProjectedPercent(double projected) => FormatProjectedPercent(projected, "pt-BR");

    public static string FormatProjectedPercent(double projected, string? languageCode)
    {
        var culture = string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase)
            ? System.Globalization.CultureInfo.GetCultureInfo("en-US")
            : System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return projected < 101 ? projected.ToString("0.0", culture) + "%" : projected.ToString("0", culture) + "%";
    }
}
