namespace AqTracker.Core;

public readonly record struct CompactGaugeLayout(double GaugeDiameter, double BackgroundDiameter);

public static class CompactGaugeLayoutPolicy
{
    public const double GaugeInset = 10d;
    public const double BackgroundInset = 14d;
    public const double MinimumFontSize = 15.18d;

    public static CompactGaugeLayout ForWindow(WidgetSize window) => new(
        Math.Max(0d, window.Height - GaugeInset),
        Math.Max(0d, window.Height - BackgroundInset));

    public static double FontSizeForWindow(WidgetSize window)
    {
        var width = Net48Compatibility.Clamp(window.Width, WidgetSizePolicy.CompactMinWidth, WidgetSizePolicy.CompactMaxWidth);
        return MinimumFontSize * width / WidgetSizePolicy.CompactMinWidth;
    }
}
