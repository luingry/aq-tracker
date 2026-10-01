using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AqTracker;

public sealed class RoundedClipBorder : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateRoundedClip();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty)
            UpdateRoundedClip();
    }

    private void UpdateRoundedClip()
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            Clip = null;
            return;
        }

        var maximumRadius = Math.Min(RenderSize.Width, RenderSize.Height) / 2d;
        var radius = Math.Min(maximumRadius, Math.Max(0d, CornerRadius.TopLeft));
        Clip = new RectangleGeometry(new Rect(RenderSize), radius, radius);
    }
}
