namespace AqTracker.Core;

public static class CircularGaugeMath
{
    public static double Clamp(double value) => !Net48Compatibility.IsFinite(value) ? 0 : Net48Compatibility.Clamp(value, 0, 100);

    public static double SweepAngle(double value) => Clamp(value) * 3.6;

    public static bool IsFullCircle(double value) => Clamp(value) >= 100;
}
