using System.Globalization;

namespace AqTracker.Core;

public static class TokenPresentation
{
    private static readonly CultureInfo BrazilianPortuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo EnglishUnitedStates = CultureInfo.GetCultureInfo("en-US");

    public static string Format(long tokens) => Format(tokens, "pt-BR");

    public static string Format(long tokens, string? languageCode)
    {
        var isEnglish = IsEnglish(languageCode);
        var culture = isEnglish ? EnglishUnitedStates : BrazilianPortuguese;
        if (tokens < 1_000) return tokens.ToString("N0", culture);
        if (tokens < 1_000_000) return FormatScaled(tokens, 1_000m, isEnglish ? "K" : "mil", culture);
        if (tokens < 1_000_000_000) return FormatScaled(tokens, 1_000_000m, isEnglish ? "M" : "mi", culture);
        return FormatScaled(tokens, 1_000_000_000m, isEnglish ? "B" : "bi", culture);
    }

    private static bool IsEnglish(string? languageCode) => string.Equals(languageCode?.Trim(), "en-US", StringComparison.OrdinalIgnoreCase);

    private static string FormatScaled(long tokens, decimal divisor, string suffix, CultureInfo culture) => $"{(tokens / divisor).ToString("0.##", culture)} {suffix}";
}
