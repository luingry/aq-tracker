using System.Text.Json;
using System.IO;
using CodexTracker.Core;

namespace CodexTracker;

public sealed record AppSettings(double Left = 80, double Top = 80, double Width = 62, double Height = 52, bool IsExpanded = false, bool IsTopmost = true, string? CodexPath = null, decimal UsdBrl = 5.50m, string Theme = "Claro", string CurrencyCode = "BRL", WidgetModeSizes? ModeSizes = null, bool IsAgentListExpanded = false, string AccentColor = AccentPalette.DefaultBaseHex, string LanguageCode = LocalizationManager.DefaultLanguageCode, IReadOnlyList<CompletedAgentWork>? UnreadAgentWorks = null, DateTimeOffset? LastUpdateCheckUtc = null, string? DeferredUpdateVersion = null, DateTimeOffset? UpdateDeferredAtUtc = null, string CompactQuotaDisplay = "both", bool ClaudeProfileEnabled = true, string ClaudeAccentColor = "#D97757");
public sealed class SettingsStore
{
    private readonly string _path;
    public string DirectoryPath => Path.GetDirectoryName(_path)!;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexTracker", "settings.json");
    }

    public AppSettings Load() { try { return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new()); } catch { return Normalize(new()); } }
    public void Save(AppSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(Normalize(settings), new JsonSerializerOptions { WriteIndented = true })); }
    public static string NormalizeCurrency(string? currencyCode) => CurrencyPresentation.Normalize(currencyCode);
    public static string NormalizeCompactQuotaDisplay(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "5h" => "5h",
        "7d" => "7d",
        _ => "both"
    };
    public static AppSettings Normalize(AppSettings settings)
    {
        var slots = WidgetSizePolicy.NormalizeSlots(settings.ModeSizes, settings.IsExpanded, new(settings.Width, settings.Height));
        var active = WidgetSizePolicy.Get(slots, settings.IsExpanded ? WidgetVisualMode.Detailed : WidgetVisualMode.Compact);
        return settings with
        {
            CurrencyCode = CurrencyPresentation.Normalize(settings.CurrencyCode),
            AccentColor = AccentPalette.Normalize(settings.AccentColor),
            ClaudeAccentColor = AccentPalette.Normalize(settings.ClaudeAccentColor ?? "#D97757"),
            LanguageCode = LocalizationManager.NormalizeLanguage(settings.LanguageCode),
            CompactQuotaDisplay = NormalizeCompactQuotaDisplay(settings.CompactQuotaDisplay),
            DeferredUpdateVersion = NormalizeDeferredUpdateVersion(settings.DeferredUpdateVersion),
            UnreadAgentWorks = (settings.UnreadAgentWorks ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item.CompletionId) && !string.IsNullOrWhiteSpace(item.ThreadId))
                .GroupBy(item => (item.Provider, item.ThreadId.ToUpperInvariant()))
                .Select(group => group.OrderByDescending(item => item.CompletedAt).First())
                .OrderByDescending(item => item.CompletedAt)
                .Take(50)
                .ToArray(),
            Width = active.Width,
            Height = active.Height,
            ModeSizes = slots
        };
    }

    // .NET Framework's unannotated reference assemblies mean string.IsNullOrWhiteSpace does not
    // flow-narrow its argument here; check nullity directly so the compiler can prove it.
    private static string? NormalizeDeferredUpdateVersion(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
