using System.Drawing;
using System.Windows;
using AqTracker.Core;
using Forms = System.Windows.Forms;

namespace AqTracker;

public partial class MainWindow
{
    private sealed class QuotaTrayEntry
    {
        public Forms.NotifyIcon Notify = new();
        public Icon? Icon;
        public string Key = "";
    }
    private readonly List<QuotaTrayEntry> _quotaTray = [];
    private bool _trayRevealActive;

    private void CheckNotificationFocus()
    {
        if (!_trayRevealActive || !_settings.ShowInNotificationArea || !IsVisible) return;
        var foreground = GetForegroundWindow();
        // A zero foreground occurs briefly during activation transitions.
        if (foreground == IntPtr.Zero) return;
        GetWindowThreadProcessId(foreground, out var processId);
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        ApplyNotificationFocus(processId == (uint)currentProcess.Id);
    }

    private void ApplyNotificationFocus(bool applicationHasFocus)
    {
        if (!_trayRevealActive || !_settings.ShowInNotificationArea || !IsVisible || applicationHasFocus) return;
        CloseWindow(this, new RoutedEventArgs());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private void UpdateFloatingOpacity()
    {
        if (!IsLoaded && _startupHidden) return;
        var isHovered = (bool)WindowSurface.GetValue(IsMouseOverProperty) ||
            AgentListPopup.IsOpen && (bool)AgentListFadeSurface.GetValue(IsMouseOverProperty);
        Opacity = _settings.FadeFloatingWidget && !_viewModel.Expanded &&
            SettingsPanel.Visibility != Visibility.Visible && !isHovered ? .5 : 1;
        AgentListFadeSurface.Opacity = Opacity;
    }

    private void UpdateQuotaTray()
    {
        if (_tray is null) return;
        if (!_settings.ShowInNotificationArea)
        {
            _tray.Visible = true;
            DisposeQuotaTray();
            return;
        }
        var rows = _viewModel.NotificationQuotas;
        while (_quotaTray.Count > rows.Count)
        {
            var last = _quotaTray[_quotaTray.Count - 1];
            last.Notify.Dispose(); last.Icon?.Dispose();
            _quotaTray.RemoveAt(_quotaTray.Count - 1);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (i == _quotaTray.Count)
            {
                var entry = new QuotaTrayEntry();
                entry.Notify.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
                _quotaTray.Add(entry);
            }
            var item = _quotaTray[i];
            var row = rows[i];
            var color = ((System.Windows.Media.SolidColorBrush)row.Brush).Color;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var key = hex + row.Percent;
            if (item.Key != key)
            {
                var icon = CreateQuotaTrayIcon(row.Percent, hex);
                var previous = item.Icon;
                item.Notify.Icon = icon;
                item.Icon = icon; item.Key = key;
                previous?.Dispose();
            }
            var tooltip = row.AccessibleName + " · " + row.Percent;
            item.Notify.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            item.Notify.ContextMenuStrip = _trayMenu;
            item.Notify.Visible = true;
        }
        _tray.Visible = rows.Count == 0;
    }

    private static Icon CreateQuotaTrayIcon(string percent, string hex)
    {
        var number = percent.Replace("%", "");
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(ColorTranslator.FromHtml(hex)))
        using (var foreground = new SolidBrush(AccentPalette.ContrastRatio(hex, "#000000") >=
            AccentPalette.ContrastRatio(hex, "#FFFFFF") ? Color.Black : Color.White))
        using (var font = new Font("Arial", number.Length > 3 ? 13 : number.Length > 2 ? 17 : 23, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            graphics.FillEllipse(background, 0, 0, 31, 31);
            graphics.DrawString(number, font, foreground, new RectangleF(0, 0, 32, 32), format);
        }
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private void DisposeQuotaTray()
    {
        foreach (var entry in _quotaTray) { entry.Notify.Dispose(); entry.Icon?.Dispose(); }
        _quotaTray.Clear();
    }
}
