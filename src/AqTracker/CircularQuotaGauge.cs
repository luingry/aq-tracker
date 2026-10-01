using System.Windows;
using System.Windows.Media;
using System.Windows.Automation.Peers;
using System.Diagnostics;
using AqTracker.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AqTracker;

public sealed class CircularQuotaGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularQuotaGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(CircularQuotaGauge), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(CircularQuotaGauge), new FrameworkPropertyMetadata(Brushes.SeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(CircularQuotaGauge), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(
        nameof(IsWorking), typeof(bool), typeof(CircularQuotaGauge), new FrameworkPropertyMetadata(false, OnIsWorkingChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ProgressBrush { get => (Brush)GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }

    private long _animationStarted;
    private bool _renderingSubscribed;
    private bool _lastGlowVisible;

    public CircularQuotaGauge()
    {
        Loaded += (_, _) => UpdateAnimationSubscription();
        Unloaded += (_, _) => StopAnimation();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = Math.Max(1, Math.Min(StrokeThickness, size / 3));
        if (size <= thickness) return;

        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var trackPen = new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var value = CircularGaugeMath.Clamp(Value);
        if (value <= 0) return;
        var progressPen = new Pen(ProgressBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (CircularGaugeMath.IsFullCircle(value))
        {
            drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
        }
        else
        {
            DrawArc(drawingContext, progressPen, center, radius, -90, CircularGaugeMath.SweepAngle(value));
        }

        DrawWorkingGlow(drawingContext, center, radius, thickness, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    private static void OnIsWorkingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var gauge = (CircularQuotaGauge)dependencyObject;
        gauge._animationStarted = Stopwatch.GetTimestamp();
        gauge.UpdateAnimationSubscription();
        gauge.InvalidateVisual();
    }

    private void UpdateAnimationSubscription()
    {
        if (IsLoaded && IsWorking && SystemParameters.ClientAreaAnimation)
        {
            if (_renderingSubscribed) return;
            _animationStarted = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
            return;
        }
        StopAnimation();
    }

    private void StopAnimation()
    {
        if (_renderingSubscribed) CompositionTarget.Rendering -= OnRendering;
        _renderingSubscribed = false;
        _lastGlowVisible = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var cycleSeconds = ElapsedSeconds() % 3d;
        var glowVisible = cycleSeconds <= 1d;
        if (glowVisible || _lastGlowVisible) InvalidateVisual();
        _lastGlowVisible = glowVisible;
    }

    private void DrawWorkingGlow(DrawingContext drawingContext, Point center, double radius, double thickness, double value)
    {
        if (!IsWorking || !SystemParameters.ClientAreaAnimation || !_renderingSubscribed) return;
        var cycleSeconds = ElapsedSeconds() % 3d;
        if (cycleSeconds > 1d) return;

        var sweep = CircularGaugeMath.SweepAngle(value);
        if (sweep <= 0) return;
        var progress = Net48Compatibility.Clamp(cycleSeconds, 0, 1);
        var head = -90 + sweep * (1 - progress);
        const double segmentDegrees = 3.2;
        for (var index = -3; index <= 3; index++)
        {
            var segmentStart = head + index * segmentDegrees;
            var clippedStart = Math.Max(-90, segmentStart);
            var clippedEnd = Math.Min(-90 + sweep, segmentStart + segmentDegrees);
            if (clippedEnd <= clippedStart) continue;
            var distance = Math.Abs(index + .5d);
            var alpha = (byte)Math.Max(0, 118 - distance * 25);
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255));
            brush.Freeze();
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            DrawArc(drawingContext, pen, center, radius, clippedStart, clippedEnd - clippedStart);
        }
    }

    private double ElapsedSeconds() => (Stopwatch.GetTimestamp() - _animationStarted) / (double)Stopwatch.Frequency;

    private static void DrawArc(DrawingContext drawingContext, Pen pen, Point center, double radius, double startDegrees, double sweepDegrees)
    {
        if (sweepDegrees <= 0) return;
        if (sweepDegrees >= 359.999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var start = PointOnCircle(center, radius, startDegrees);
        var end = PointOnCircle(center, radius, startDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
