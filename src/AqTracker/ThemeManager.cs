using System.Windows.Media;
using AqTracker.Core;

namespace AqTracker;

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
        Set("UiSoft", dark ? "#454545" : "#DEDEDE");
        Set("SwitchActiveBackground", dark ? "#426B54" : "#B8DCC5");
        Set("Sage", accent.SoftHex);
        Set("UiAccent", dark ? "#D0D0D0" : "#595959");
        Set("OnUiAccent", dark ? "#1A1A1A" : "#FFFFFF");
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
        Set("HoverSurface", dark ? "#484848" : "#E5E5E5");
        Set("CardSurface", dark ? "#FF353535" : "#FFFBFBF9");
        Set("CardBorder", dark ? "#FF424242" : "#FFE0E4E1");
        Set("OnAccent", ReadableTextOn(accent.AccentHex));
        Set("OnClaudeAccent", ReadableTextOn(claude.AccentHex));
        Set("Danger", dark ? "#FFFF8A80" : "#FFB42318");
        Set("DangerSoft", dark ? "#33FF8A80" : "#1AB42318");
    }

    // Primary buttons are filled with a user-chosen accent, so their label picks whichever
    // of near-black or white keeps the higher contrast against that fill.
    private static string ReadableTextOn(string backgroundHex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(backgroundHex);
        static double Linear(byte channel) { var value = channel / 255d; return value <= .03928 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return (luminance + .05) / .05 >= 1.05 / (luminance + .05) ? "#FF151A18" : "#FFFFFFFF";
    }
    private static void Set(string key, string hex) => System.Windows.Application.Current.Resources[key] =
        new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    private static void SetColor(string key, string hex) => System.Windows.Application.Current.Resources[key] =
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
}
