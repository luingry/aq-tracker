using AqTracker.Core;

namespace AqTracker;

/// <summary>Structured, localized data shared by all token-usage tooltips.</summary>
public sealed record TokenUsageTooltipLine(string Label, string Tokens, string Cost, double Fraction);

public sealed record TokenUsageTooltip(string Title, IReadOnlyList<TokenUsageTooltipLine> Categories, TokenUsageTooltipLine Total, string EstimateNote)
{
    public static TokenUsageTooltip Create(string title, TokenUsageBreakdown breakdown, bool priced, decimal usdBrl, string currencyCode)
    {
        var languageCode = LocalizationManager.CurrentLanguageCode;
        string Cost(decimal usd) => priced
            ? CurrencyPresentation.FormatCost(usd, usd * usdBrl, currencyCode, languageCode)
            : LocalizationManager.Text("NoTariff");
        var totalTokens = breakdown.TotalTokens;
        double Fraction(long tokens) => totalTokens <= 0
            ? 0d
            : Math.Max(0d, Math.Min(1d, tokens / (double)totalTokens));
        TokenUsageTooltipLine Line(string key, long tokens, decimal usd) => new(
            LocalizationManager.Text(key),
            TokenPresentation.Format(tokens, languageCode),
            Cost(usd),
            Fraction(tokens));

        return new(
            title,
            [
                Line("CachedRead", breakdown.CachedReadTokens, breakdown.CachedReadCostUsd),
                Line("Input", breakdown.InputTokens, breakdown.InputCostUsd),
                Line("Output", breakdown.OutputTokens, breakdown.OutputCostUsd),
                Line("Reasoning", breakdown.ReasoningTokens, breakdown.ReasoningCostUsd)
            ],
            Line("Total", breakdown.TotalTokens, breakdown.TotalCostUsd),
            priced ? LocalizationManager.Text("EstimatedValues") : LocalizationManager.Text("NoTariff"));
    }

    // Kept for accessibility and existing text consumers; presentation never parses this string.
    public string ToPlainText() => string.Join(Environment.NewLine,
        new[] { Title }
            .Concat(Categories.SelectMany(line => new[] { $"{line.Label}: {line.Tokens}", line.Cost }))
            .Concat(new[] { $"{Total.Label}: {Total.Tokens}", Total.Cost }));
}
