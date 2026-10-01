namespace AqTracker.Core;

public enum WidgetVisualMode { Compact, Detailed, Settings }

public readonly record struct WidgetSize(double Width, double Height);

public sealed record WidgetModeSizes(WidgetSize? Compact = null, WidgetSize? Detailed = null, WidgetSize? Settings = null);

public static class WidgetSizePolicy
{
    public const double CompactAspectRatio = 62d / 52d;
    public const double CompactMinWidth = 62d;
    public const double CompactMaxWidth = 100d;
    public const double DetailedWidth = 300d;
    public const double DetailedMinHeight = 260d;
    public const double DetailedDefaultHeight = 360d;
    public const double DetailedMaxHeight = 720d;
    // Settings opens directly at one fixed height and scrolls its content; it never grows to fit.
    public const double SettingsHeight = 572d;
    public const double SettingsMaxHeight = 720d;

    public static WidgetSize Default(WidgetVisualMode mode) => mode switch
    {
        WidgetVisualMode.Compact => new(CompactMinWidth, CompactMinWidth / CompactAspectRatio),
        WidgetVisualMode.Detailed => new(DetailedWidth, DetailedDefaultHeight),
        WidgetVisualMode.Settings => new(DetailedWidth, SettingsHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static WidgetSize Normalize(WidgetVisualMode mode, WidgetSize? size)
    {
        if (size is not { } value || !IsFinitePositive(value.Width) || !IsFinitePositive(value.Height)) return Default(mode);

        return mode switch
        {
            WidgetVisualMode.Compact => new(Net48Compatibility.Clamp(value.Width, CompactMinWidth, CompactMaxWidth), Net48Compatibility.Clamp(value.Width, CompactMinWidth, CompactMaxWidth) / CompactAspectRatio),
            WidgetVisualMode.Detailed => new(DetailedWidth, Net48Compatibility.Clamp(value.Height, DetailedMinHeight, DetailedMaxHeight)),
            WidgetVisualMode.Settings => new(DetailedWidth, Net48Compatibility.Clamp(value.Height, SettingsHeight, SettingsMaxHeight)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static WidgetModeSizes NormalizeSlots(WidgetModeSizes? slots, bool isExpanded, WidgetSize legacySize)
    {
        var legacyMode = isExpanded ? WidgetVisualMode.Detailed : WidgetVisualMode.Compact;
        return new(
            Normalize(WidgetVisualMode.Compact, slots?.Compact ?? (legacyMode == WidgetVisualMode.Compact ? legacySize : null)),
            Normalize(WidgetVisualMode.Detailed, slots?.Detailed ?? (legacyMode == WidgetVisualMode.Detailed ? legacySize : null)),
            Normalize(WidgetVisualMode.Settings, slots?.Settings));
    }

    public static WidgetSize Get(WidgetModeSizes slots, WidgetVisualMode mode) => mode switch
    {
        WidgetVisualMode.Compact => Normalize(mode, slots.Compact),
        WidgetVisualMode.Detailed => Normalize(mode, slots.Detailed),
        WidgetVisualMode.Settings => Normalize(mode, slots.Settings),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    // UI mode switches must select the destination slot, never dimensions
    // temporarily held by the previous WPF layout constraints.
    public static WidgetSize SelectModeSize(WidgetModeSizes slots, WidgetVisualMode destination) => Get(slots, destination);

    public static double DetailedMaxHeightForContent(double contentHeight) =>
        !IsFinitePositive(contentHeight)
            ? DetailedMaxHeight
            : Net48Compatibility.Clamp(Math.Ceiling(contentHeight), DetailedMinHeight, DetailedMaxHeight);

    public static double DetailedHeightForContent(double contentHeight, double workAreaHeight)
    {
        var safeMaximum = IsFinitePositive(workAreaHeight)
            ? Math.Min(DetailedMaxHeight, Math.Max(1d, Math.Floor(workAreaHeight)))
            : DetailedMaxHeight;
        var safeMinimum = Math.Min(DetailedMinHeight, safeMaximum);
        var requested = IsFinitePositive(contentHeight) ? Math.Ceiling(contentHeight) : DetailedMinHeight;
        return Net48Compatibility.Clamp(requested, safeMinimum, safeMaximum);
    }

    public static double SettingsHeightForWorkArea(double workAreaHeight) =>
        IsFinitePositive(workAreaHeight) ? Math.Min(SettingsHeight, Math.Max(1d, Math.Floor(workAreaHeight))) : SettingsHeight;

    public static WidgetModeSizes With(WidgetModeSizes slots, WidgetVisualMode mode, WidgetSize size)
    {
        var normalized = Normalize(mode, size);
        return mode switch
        {
            WidgetVisualMode.Compact => slots with { Compact = normalized },
            WidgetVisualMode.Detailed => slots with { Detailed = normalized },
            WidgetVisualMode.Settings => slots with { Settings = normalized },
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static bool IsFinitePositive(double value) => Net48Compatibility.IsFinite(value) && value > 0;
}
