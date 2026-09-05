using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using CodexTracker.Core;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace CodexTracker;

public sealed class DailyUsageChart : FrameworkElement
{
    private readonly List<BarHit> _barHits = [];
    private readonly WpfToolTip _tooltip = new() { Placement = PlacementMode.Mouse, StaysOpen = true };
    private int _hoveredIndex = -1;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty QuotaSeriesProperty = DependencyProperty.Register(
        nameof(QuotaSeries), typeof(IEnumerable), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty QuotaBrushProperty = DependencyProperty.Register(
        nameof(QuotaBrush), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TooltipSurfaceProperty = DependencyProperty.Register(
        nameof(TooltipSurface), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TooltipTextBrushProperty = DependencyProperty.Register(
        nameof(TooltipTextBrush), typeof(MediaBrush), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata(MediaBrushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrencyCodeProperty = DependencyProperty.Register(
        nameof(CurrencyCode), typeof(string), typeof(DailyUsageChart),
        new FrameworkPropertyMetadata("BRL", FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Series { get => (IEnumerable?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IEnumerable? QuotaSeries { get => (IEnumerable?)GetValue(QuotaSeriesProperty); set => SetValue(QuotaSeriesProperty, value); }
    public MediaBrush QuotaBrush { get => (MediaBrush)GetValue(QuotaBrushProperty); set => SetValue(QuotaBrushProperty, value); }
    public MediaBrush BarBrush { get => (MediaBrush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public MediaBrush TrackBrush { get => (MediaBrush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public MediaBrush LabelBrush { get => (MediaBrush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public MediaBrush TooltipSurface { get => (MediaBrush)GetValue(TooltipSurfaceProperty); set => SetValue(TooltipSurfaceProperty, value); }
    public MediaBrush TooltipTextBrush { get => (MediaBrush)GetValue(TooltipTextBrushProperty); set => SetValue(TooltipTextBrushProperty, value); }
    public string CurrencyCode { get => (string)GetValue(CurrencyCodeProperty); set => SetValue(CurrencyCodeProperty, value); }

    public DailyUsageChart()
    {
        IsHitTestVisible = true;
        _tooltip.SetResourceReference(StyleProperty, "TokenUsageToolTip");
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { _hoveredIndex = -1; _tooltip.IsOpen = false; InvalidateVisual(); };
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(MediaBrushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var values = ReadValues();
        var days = Math.Max(DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month), values.Count);
        var quota = ReadQuotaValues();
        var maximum = values.Count == 0 ? 0 : values.Max(value => value.Tokens);
        const double labelHeight = 14;
        const double axisWidth = 23;
        const double verticalPadding = 5;
        var chartHeight = Math.Max(1, ActualHeight - labelHeight - verticalPadding * 2);
        var chartTop = verticalPadding;
        var gap = 1.5;
        var plotWidth = Math.Max(1, ActualWidth - axisWidth);
        var width = Math.Max(1.5, (plotWidth - gap * (days - 1)) / days);
        var baseline = new MediaPen(TrackBrush, 1);
        drawingContext.DrawLine(baseline, new WpfPoint(axisWidth, chartTop + chartHeight - .5), new WpfPoint(ActualWidth, chartTop + chartHeight - .5));
        drawingContext.DrawLine(baseline, new WpfPoint(axisWidth - .5, chartTop), new WpfPoint(axisWidth - .5, chartTop + chartHeight));
        for (var tick = 0; tick <= 100; tick += 20)
        {
            var y = chartTop + chartHeight - chartHeight * tick / 100d;
            var tickText = CreateText(tick.ToString(CultureInfo.InvariantCulture) + "%", new Typeface(new System.Windows.Media.FontFamily("./assets/fonts/#Source Sans 3"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal));
            drawingContext.DrawLine(baseline, new WpfPoint(axisWidth - 3, y), new WpfPoint(axisWidth, y));
            drawingContext.DrawText(tickText, new WpfPoint(Math.Max(0, axisWidth - tickText.Width - 4), y - tickText.Height / 2));
        }

        _barHits.Clear();
        for (var index = 0; index < days; index++)
        {
            var value = index < values.Count ? values[index] : DailyPoint.Zero(index + 1);
            var height = maximum <= 0 ? 2 : Math.Max(2, value.Tokens / maximum * (chartHeight - 3));
            var x = axisWidth + index * (width + gap);
            var brush = value.Tokens > 0 ? BarBrush : TrackBrush;
            var barRect = new Rect(x, chartTop + chartHeight - height, width, height);
            drawingContext.DrawRoundedRectangle(brush, null, barRect, 1.5, 1.5);
            _barHits.Add(new(new Rect(x, chartTop, Math.Max(width, width + gap), chartHeight), value, index < quota.Count ? quota[index].UsedPercent : null));
        }

        var quotaPoints = quota.Select((value, index) => value.UsedPercent is { } percent
            ? new WpfPoint(axisWidth + index * (width + gap) + width / 2, chartTop + chartHeight - chartHeight * percent / 100d) : (WpfPoint?)null).ToArray();
        foreach (var segment in QuotaCurve.Segments(quotaPoints))
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(segment[0], false, false);
                for (var index = 1; index < segment.Count; index++)
                {
                    var previous = segment[index - 1]; var current = segment[index];
                    var before = index > 1 ? segment[index - 2] : previous;
                    var after = index + 1 < segment.Count ? segment[index + 1] : current;
                    var minY = Math.Min(previous.Y, current.Y); var maxY = Math.Max(previous.Y, current.Y);
                    var slopeStart = (current.Y - before.Y) / Math.Max(1, current.X - before.X);
                    var slopeEnd = (after.Y - previous.Y) / Math.Max(1, after.X - previous.X);
                    var first = new WpfPoint(previous.X + (current.X - previous.X) / 3, Math.Max(minY, Math.Min(maxY, previous.Y + slopeStart * (current.X - previous.X) / 3)));
                    var second = new WpfPoint(current.X - (current.X - previous.X) / 3, Math.Max(minY, Math.Min(maxY, current.Y - slopeEnd * (current.X - previous.X) / 3)));
                    context.BezierTo(first, second, current, true, false);
                }
            }
            drawingContext.DrawGeometry(null, new MediaPen(QuotaBrush, 1.5), geometry);
        }
        foreach (var point in quotaPoints.Where(point => point.HasValue).Select(point => point!.Value))
            drawingContext.DrawEllipse(QuotaBrush, null, point, 2, 2);

        var typeface = new Typeface(new System.Windows.Media.FontFamily("./assets/fonts/#Source Sans 3"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        DrawLabel(drawingContext, "1", axisWidth, chartTop + chartHeight + 2, typeface);
        var last = days.ToString(CultureInfo.InvariantCulture);
        var formatted = CreateText(last, typeface);
        drawingContext.DrawText(formatted, new WpfPoint(Math.Max(axisWidth, ActualWidth - formatted.Width), chartTop + chartHeight + 2));
        if (_hoveredIndex >= 0 && _hoveredIndex < _barHits.Count && _barHits[_hoveredIndex].Point.Day <= DateTime.Today.Day)
            ShowTooltip(_barHits[_hoveredIndex]);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    private List<DailyPoint> ReadValues()
    {
        var result = new List<DailyPoint>();
        if (Series is null) return result;
        var fallbackDay = 1;
        foreach (var item in Series)
        {
            if (item is null) { result.Add(DailyPoint.Zero(fallbackDay++)); continue; }
            if (TryNumber(item, out var direct)) { result.Add(new(fallbackDay++, Math.Max(0, direct), 0, 0, TokenUsageBreakdown.Zero)); continue; }
            var type = item.GetType();
            var dayValue = type.GetProperty("Day")?.GetValue(item);
            var day = dayValue is DateTime date ? date.Day : fallbackDay;
            var tokenProperty = type.GetProperty("Tokens") ?? type.GetProperty("Value") ?? type.GetProperty("Usage");
            _ = TryNumber(tokenProperty?.GetValue(item), out var tokens);
            _ = TryDecimal(type.GetProperty("UsdCost")?.GetValue(item), out var usd);
            _ = TryDecimal(type.GetProperty("BrlCost")?.GetValue(item), out var brl);
            var breakdown = type.GetProperty("Breakdown")?.GetValue(item) as TokenUsageBreakdown ?? TokenUsageBreakdown.Zero;
            result.Add(new(day, Math.Max(0, tokens), Math.Max(0, usd), Math.Max(0, brl), breakdown));
            fallbackDay++;
        }
        return result;
    }

    private List<DailyQuotaPoint> ReadQuotaValues()
    {
        var result = new List<DailyQuotaPoint>(); if (QuotaSeries is null) return result;
        var day = 1;
        foreach (var item in QuotaSeries)
        {
            var type = item?.GetType(); var date = type?.GetProperty("Day")?.GetValue(item) as DateTime?;
            var raw = type?.GetProperty("UsedPercent")?.GetValue(item);
            result.Add(new(date?.Day ?? day++, TryNumber(raw, out var value) ? Net48Compatibility.Clamp(value, 0, 100) : null));
        }
        return result;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var next = _barHits.FindIndex(hit => hit.Bounds.Contains(position));
        if (next == _hoveredIndex) return;
        _hoveredIndex = next;
        if (next >= 0 && _barHits[next].Point.Day <= DateTime.Today.Day) ShowTooltip(_barHits[next]);
        else _tooltip.IsOpen = false;
        InvalidateVisual();
    }

    private void ShowTooltip(BarHit hit)
    {
        var exchangeRate = hit.Point.UsdCost > 0 ? hit.Point.BrlCost / hit.Point.UsdCost : 0;
        var title = LocalizationManager.Format("DayNumber", hit.Point.Day) + " · " + (hit.UsedPercent is { } used ? LocalizationManager.Text("WeeklyQuota") + " " + used.ToString("0.#", CultureInfo.CurrentUICulture) + "%" : LocalizationManager.Text("NoQuotaRecord"));
        _tooltip.Content = TokenUsageTooltip.Create(title, hit.Point.Breakdown, true, exchangeRate, CurrencyCode);
        _tooltip.IsOpen = true;
    }

    private static bool TryNumber(object? value, out double number)
    {
        try { number = value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); return value is not null; }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { number = 0; return false; }
    }

    private static bool TryDecimal(object? value, out decimal number)
    {
        try { number = value is null ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture); return value is not null; }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { number = 0; return false; }
    }

    private void DrawLabel(DrawingContext context, string text, double x, double y, Typeface typeface) =>
        context.DrawText(CreateText(text, typeface), new WpfPoint(x, y));

    private FormattedText CreateText(string text, Typeface typeface) => new(
        text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, typeface, 8, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private sealed record DailyPoint(int Day, double Tokens, decimal UsdCost, decimal BrlCost, TokenUsageBreakdown Breakdown)
    {
        public static DailyPoint Zero(int day) => new(day, 0, 0, 0, TokenUsageBreakdown.Zero);
    }
    private sealed record DailyQuotaPoint(int Day, double? UsedPercent);
    private sealed record BarHit(Rect Bounds, DailyPoint Point, double? UsedPercent);
}

public static class QuotaCurve
{
    // Consecutive points only: the short Catmull-Rom-like tangent is clamped to retain monotonic bounds.
    public static IEnumerable<IReadOnlyList<WpfPoint>> Segments(IEnumerable<WpfPoint?> points)
    {
        var segment = new List<WpfPoint>();
        foreach (var point in points)
        {
            if (point is { } value) segment.Add(value);
            else { if (segment.Count > 1) yield return segment.ToArray(); segment.Clear(); }
        }
        if (segment.Count > 1) yield return segment.ToArray();
    }
}
