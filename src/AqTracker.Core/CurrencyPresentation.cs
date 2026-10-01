using System.Globalization;

namespace AqTracker.Core;

public static class CurrencyPresentation
{
    private static readonly CultureInfo BrazilianPortuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo EnglishUnitedStates = CultureInfo.GetCultureInfo("en-US");

    public static string Normalize(string? currencyCode) => string.Equals(currencyCode, "USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "BRL";

    public static string FormatCost(decimal usd, decimal brl, string? currencyCode) => FormatCost(usd, brl, currencyCode, "pt-BR");

    public static string FormatCost(decimal usd, decimal brl, string? currencyCode, string? languageCode)
    {
        var culture = string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase)
            ? EnglishUnitedStates
            : BrazilianPortuguese;

        return Normalize(currencyCode) == "USD"
            ? $"US$ {usd.ToString("N2", culture)}"
            : $"R$ {brl.ToString("N2", culture)}";
    }

    public static string FormatMonthlyCost(decimal monthUsd, decimal monthBrl, string? currencyCode) => FormatCost(monthUsd, monthBrl, currencyCode);

    public static string FormatMonthlyCost(decimal monthUsd, decimal monthBrl, string? currencyCode, string? languageCode) => FormatCost(monthUsd, monthBrl, currencyCode, languageCode);
}
