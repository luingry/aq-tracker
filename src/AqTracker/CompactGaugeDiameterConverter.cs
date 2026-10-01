using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AqTracker.Core;

namespace AqTracker;

public sealed class CompactGaugeDiameterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var dimension = value is double actualDimension ? actualDimension : 0d;
        if (string.Equals(parameter?.ToString(), "FontSize", StringComparison.OrdinalIgnoreCase))
            return CompactGaugeLayoutPolicy.FontSizeForWindow(new WidgetSize(dimension, 0d));
        if (string.Equals(parameter?.ToString(), "WindowHeight", StringComparison.OrdinalIgnoreCase))
            return dimension / WidgetSizePolicy.CompactAspectRatio;

        var layout = CompactGaugeLayoutPolicy.ForWindow(new WidgetSize(0d, dimension));
        return string.Equals(parameter?.ToString(), "Background", StringComparison.OrdinalIgnoreCase)
            ? layout.BackgroundDiameter
            : layout.GaugeDiameter;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

public sealed class CompactQuotaGaugeSizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double actual ? actual : WidgetSizePolicy.CompactMinWidth;
        var count = values.Length > 1 && values[1] is int visible ? visible : 1;
        var diameter = count > 1
            ? Math.Max(0d, (width - 6d * count) / count)
            : CompactGaugeLayoutPolicy.ForWindow(new WidgetSize(0d, width / WidgetSizePolicy.CompactAspectRatio)).GaugeDiameter;
        return parameter?.ToString() switch
        {
            "HostHeight" => diameter + CompactGaugeLayoutPolicy.GaugeInset,
            "Label" => diameter * .19d,
            "Percent" => diameter * .36d,
            _ => diameter
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [System.Windows.Data.Binding.DoNothing, System.Windows.Data.Binding.DoNothing];
}
