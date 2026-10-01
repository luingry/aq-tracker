using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AqTracker.Core;

namespace AqTracker;

internal static class Backdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmncrpDisabled = 1;
    private const int DwmncrpEnabled = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark, WidgetVisualMode visualMode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var darkMode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            var composition = BackdropCompositionPolicy.ForMode(visualMode);
            var nonClientRendering = composition.NonClientRendering == BackdropNonClientRendering.Disabled ? DwmncrpDisabled : DwmncrpEnabled;
            _ = DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref nonClientRendering, sizeof(int));
            var rounded = composition.CornerPreference == BackdropCornerPreference.DoNotRound ? DwmwcpDoNotRound : DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
            var borderColor = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
