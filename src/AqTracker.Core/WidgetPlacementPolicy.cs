namespace AqTracker.Core;

public readonly record struct WidgetScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class WidgetPlacementPolicy
{
    public static WidgetScreenRect Restore(WidgetScreenRect savedBounds, IReadOnlyList<WidgetScreenRect> workAreas)
    {
        if (workAreas.Count == 0) return savedBounds;

        if (workAreas.Any(workArea => Intersects(savedBounds, workArea))) return savedBounds;

        var nearest = workAreas
            .OrderBy(workArea => SquaredDistance(savedBounds, workArea))
            .First();
        var left = Math.Max(nearest.Left, Math.Min(savedBounds.Left, nearest.Right - savedBounds.Width));
        var top = Math.Max(nearest.Top, Math.Min(savedBounds.Top, nearest.Bottom - savedBounds.Height));
        return new WidgetScreenRect(left, top, left + savedBounds.Width, top + savedBounds.Height);
    }

    private static bool Intersects(WidgetScreenRect left, WidgetScreenRect right) =>
        left.Left < right.Right && right.Left < left.Right && left.Top < right.Bottom && right.Top < left.Bottom;

    private static long SquaredDistance(WidgetScreenRect bounds, WidgetScreenRect workArea)
    {
        var horizontal = bounds.Right < workArea.Left ? (long)workArea.Left - bounds.Right : bounds.Left > workArea.Right ? (long)bounds.Left - workArea.Right : 0;
        var vertical = bounds.Bottom < workArea.Top ? (long)workArea.Top - bounds.Bottom : bounds.Top > workArea.Bottom ? (long)bounds.Top - workArea.Bottom : 0;
        return horizontal * horizontal + vertical * vertical;
    }
}
