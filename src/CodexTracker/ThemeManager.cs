using System.Windows.Media;
using CodexTracker.Core;

namespace CodexTracker;

internal static class ThemeManager
{
    public static void Apply(string theme) => Apply(theme, AccentPalette.DefaultBaseHex);

    public static void Apply(string theme, string? accentColor, string? claudeAccentColor = "#D97757")
    {
        var dark = string.Equals(theme, "Escuro", StringComparison.OrdinalIgnoreCase);
        var accent = AccentPalette.Create(accentColor, dark);
        var claude = AccentPalette.Create(claudeAccentColor, dark);
        Set("ClaudeAccent", claude.AccentHex);
        Set("ClaudeMetadataAccent", claude.AgentMetadataHex);
        Set("Porcelain", dark ? AccentPalette.DarkSurfaceHex : "#F7F7F4");
        Set("Ink", dark ? "#F1F4F2" : "#202523");
        Set("SoftInk", dark ? "#AEB9B4" : "#59635F");
        Set("Sage", accent.SoftHex);
        Set("Apricot", dark ? "#4C3B31" : "#F0DED0");
        Set("Lavender", dark ? "#37343D" : "#EAE8EF");
        Set("Accent", accent.AccentHex);
        Set("AgentMetadataAccent", accent.AgentMetadataHex);
        SetColor("AccentGlow", accent.GlowHex);
        Set("RiskWarning", dark ? "#FFD166" : "#B66A00");
        Set("GaugeTrack", dark ? "#46504C" : "#CCD5D1");
        Set("DetailedSurface", dark ? "#FF2D2D2D" : "#FFF7F7F4");
        Set("GlassSurface", dark ? "#FF2D2D2D" : "#FFF7F7F4");
        Set("GlassLavender", dark ? "#FF363636" : "#FFEAE8EF");
        Set("SettingsSurface", dark ? "#FF2D2D2D" : "#FFF2F2EE");
        Set("InputSurface", dark ? "#FF3A3A3A" : "#FFE7EBE8");
        Set("HoverSurface", accent.HoverHex);
    }
    private static void Set(string key, string hex) => System.Windows.Application.Current.Resources[key] =
        new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    private static void SetColor(string key, string hex) => System.Windows.Application.Current.Resources[key] =
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
}
