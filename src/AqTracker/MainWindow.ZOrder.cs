using System.Windows;
using System.Windows.Interop;

namespace AqTracker;

public partial class MainWindow
{
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TopmostProperty)
        {
            ShowInTaskbar = !Topmost;
            ApplyTrayOnlyWindowStyle();
            SynchronizeAgentListTopmost();
        }
    }

    private void SynchronizeAgentListTopmost()
    {
        // WPF Popup has its own HWND and defaults to topmost independently of its owner.
        if (AgentListPopup?.IsOpen != true ||
            PresentationSource.FromVisual(AgentListPopup.Child) is not HwndSource source) return;
        const long topmostStyle = 0x8;
        var isTopmost = (GetExtendedWindowStyle(source.Handle).ToInt64() & topmostStyle) != 0;
        if (isTopmost == Topmost) return;
        SetWindowPos(source.Handle, new IntPtr(Topmost ? -1 : -2), 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }
}
