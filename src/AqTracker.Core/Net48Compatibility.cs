namespace AqTracker.Core;

public static class Net48Compatibility
{
    public static double Clamp(double value, double minimum, double maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public static TValue GetValueOrDefault<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values, TKey key)
        where TKey : notnull => values.TryGetValue(key, out var value) ? value : default!;

    public static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty);
}

public static class RuntimePaths
{
    public static string ExecutablePath
    {
        get
        {
            try { using (var process = System.Diagnostics.Process.GetCurrentProcess()) return process.MainModule?.FileName ?? AppDomain.CurrentDomain.BaseDirectory; }
            catch { return AppDomain.CurrentDomain.BaseDirectory; }
        }
    }
}
