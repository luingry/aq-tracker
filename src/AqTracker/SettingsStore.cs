using System.Text.Json;
using System.IO;
using AqTracker.Core;

namespace AqTracker;

public sealed record AppSettings(double Left = 80, double Top = 80, double Width = 62, double Height = 52, bool IsExpanded = false, bool IsTopmost = true, string? CodexPath = null, decimal UsdBrl = 5.50m, string Theme = "Escuro", string CurrencyCode = "BRL", WidgetModeSizes? ModeSizes = null, bool IsAgentListExpanded = false, string AccentColor = AccentPalette.DefaultBaseHex, string LanguageCode = LocalizationManager.DefaultLanguageCode, IReadOnlyList<CompletedAgentWork>? UnreadAgentWorks = null, DateTimeOffset? LastUpdateCheckUtc = null, string? DeferredUpdateVersion = null, DateTimeOffset? UpdateDeferredAtUtc = null, string CompactQuotaDisplay = "both", bool ClaudeProfileEnabled = true, string ClaudeAccentColor = "#D97757", bool StartMinimizedWithWindows = false, bool ShowInNotificationArea = false, bool FadeFloatingWidget = false, int FloatingOpacityPercent = 50);
public sealed class SettingsStore
{
    private readonly string _path;
    public string DirectoryPath => Path.GetDirectoryName(_path)!;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(UserFolders.ApplicationData, "AqTracker", "settings.json");
        if (path is null) SanitizedLogger.Write("Settings path: " + _path);
    }

    public AppSettings Load()
    {
        var primary = Read(_path, out var primaryExists);
        if (primary is not null) return primary;
        var backup = Read(_path + ".bak", out var backupExists);
        if (backup is not null)
        {
            SanitizedLogger.Write("Settings recovered from backup.");
            return backup;
        }
        if (primaryExists || backupExists)
            throw new InvalidDataException("Settings and backup are invalid; existing user data was preserved.");
        return Normalize(new());
    }

    public async Task<AppSettings> LoadWhenAvailableAsync(Func<Task>? retryDelay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Load(); }
            catch (Exception error) when (attempt < 9 &&
                (error is IOException && error is not InvalidDataException || error is UnauthorizedAccessException))
            {
                SanitizedLogger.Write("Settings temporarily unavailable at startup; retry=" + (attempt + 1) + "; error=" + error.GetType().Name);
                await (retryDelay?.Invoke() ?? Task.Delay(1000));
            }
        }
    }

    private static AppSettings? Read(string path, out bool exists)
    {
        exists = true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var settings = JsonSerializer.Deserialize<AppSettings>(stream);
            return settings is null ? null : Normalize(settings);
        }
        catch (FileNotFoundException) { exists = false; return null; }
        catch (DirectoryNotFoundException) { exists = false; return null; }
        catch (JsonException) { SanitizedLogger.Write("Invalid settings JSON; attempting backup recovery."); return null; }
        // Access/sharing failures must not be mistaken for a fresh install.
    }

    public void Save(AppSettings settings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Normalize(settings), new JsonSerializerOptions { WriteIndented = true });
        // Commit a complete recovery copy before replacing the primary file.
        AtomicFile.Write(_path + ".bak", bytes);
        AtomicFile.Write(_path, bytes);
    }
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
            FloatingOpacityPercent = Math.Max(10, Math.Min(90, settings.FloatingOpacityPercent)),
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
