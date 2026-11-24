using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AqTracker;

/// <summary>
/// Per-pixel transparency through DWM instead of a WPF layered window (AllowsTransparency).
/// A layered window repaints with UpdateLayeredWindow on every rendered frame, and while one does
/// that continuously (the working spinner and gauge glow animate forever) every injected
/// left-button-down in the whole session waits about one frame inside SendInput (~11 ms),
/// capping autoclickers near 75 clicks/s. A normal window whose client area is all DWM frame
/// presents through DirectX like any other app and leaves input alone. See ERRORS.md (0.32.2).
/// </summary>
internal static class DwmTransparency
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Call on a non-layered HwndSource (window or popup) whose WPF background is transparent.</summary>
    public static void Apply(HwndSource? source)
    {
        if (source?.CompositionTarget is null || source.Handle == IntPtr.Zero) return;
        source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        try { _ = DwmExtendFrameIntoClientArea(source.Handle, ref margins); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
