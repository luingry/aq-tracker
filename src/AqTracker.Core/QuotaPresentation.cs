using System.Globalization;

namespace AqTracker.Core;

/// <summary>Formats the peripheral quota number without changing protocol semantics.</summary>
public static class QuotaPresentation
{
    public static string FormatWeeklyRemaining(QuotaWindow? weekly) => weekly is null || !weekly.HasUsage
        ? "--"
        : FormatPercent(weekly.RemainingPercent);

    private static string FormatPercent(double value) => Math.Abs(value - Math.Round(value)) < 0.001
        ? Math.Round(value).ToString(CultureInfo.InvariantCulture) + "%"
        : value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
