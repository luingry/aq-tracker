namespace CodexTracker.Core;

[Flags]
public enum ResizeHandle
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

public readonly record struct ResizeVector(double X, double Y);

public readonly record struct ResizeBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public readonly record struct ResizeWorkArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Pure manual-resize geometry that keeps the edge opposite the dragged handle fixed.</summary>
public static class ManualResizeGeometry
{
    public const double CompactAspectRatio = 62d / 52d;

    public static ResizeBounds ResizeCompact(
        ResizeBounds start,
        ResizeVector delta,
        ResizeHandle handle,
        ResizeWorkArea workArea,
        double minWidth = 62d,
        double maxWidth = 320d,
        double aspectRatio = CompactAspectRatio,
        double heightOffset = 0d)
    {
        var horizontalDelta = handle.HasFlag(ResizeHandle.Left) ? -delta.X : handle.HasFlag(ResizeHandle.Right) ? delta.X : 0;
        var verticalDelta = (handle.HasFlag(ResizeHandle.Top) ? -delta.Y : delta.Y) * aspectRatio;
        var requestedWidth = start.Width + (Math.Abs(horizontalDelta) >= Math.Abs(verticalDelta) ? horizontalDelta : verticalDelta);
        var maximumWidth = Math.Min(maxWidth, CompactMaximumWidth(start, handle, workArea, aspectRatio, heightOffset));
        var width = Net48Compatibility.Clamp(requestedWidth, minWidth, maximumWidth);
        var height = width / aspectRatio + heightOffset;
        var left = handle.HasFlag(ResizeHandle.Left) ? start.Right - width : start.Left;
        var top = handle.HasFlag(ResizeHandle.Top) ? start.Bottom - height : start.Top;
        return new ResizeBounds(left, top, width, height);
    }

    public static ResizeBounds ResizeVertical(
        ResizeBounds start,
        ResizeVector delta,
        ResizeHandle handle,
        ResizeWorkArea workArea,
        double minHeight,
        double maxHeight)
    {
        var requestedHeight = start.Height + (handle.HasFlag(ResizeHandle.Top) ? -delta.Y : delta.Y);
        var maximumHeight = handle.HasFlag(ResizeHandle.Top)
            ? Math.Min(maxHeight, start.Bottom - workArea.Top)
            : Math.Min(maxHeight, workArea.Bottom - start.Top);
        var height = Net48Compatibility.Clamp(requestedHeight, minHeight, maximumHeight);
        var top = handle.HasFlag(ResizeHandle.Top) ? start.Bottom - height : start.Top;
        return new ResizeBounds(start.Left, top, start.Width, height);
    }

    private static double CompactMaximumWidth(ResizeBounds start, ResizeHandle handle, ResizeWorkArea workArea, double aspectRatio, double heightOffset)
    {
        var maximumWidth = double.PositiveInfinity;
        if (handle.HasFlag(ResizeHandle.Left)) maximumWidth = Math.Min(maximumWidth, start.Right - workArea.Left);
        if (handle.HasFlag(ResizeHandle.Right)) maximumWidth = Math.Min(maximumWidth, workArea.Right - start.Left);
        if (handle.HasFlag(ResizeHandle.Top)) maximumWidth = Math.Min(maximumWidth, (start.Bottom - workArea.Top - heightOffset) * aspectRatio);
        if (handle.HasFlag(ResizeHandle.Bottom)) maximumWidth = Math.Min(maximumWidth, (workArea.Bottom - start.Top - heightOffset) * aspectRatio);
        return maximumWidth;
    }
}
