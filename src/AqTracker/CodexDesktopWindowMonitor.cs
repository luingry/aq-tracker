using System.Runtime.InteropServices;
using System.IO;
using AqTracker.Core;

namespace AqTracker;

public readonly record struct CodexDesktopWindowState(bool IsForeground, bool IsMinimized);
public readonly record struct DesktopWindowState(AgentProvider? Provider, bool IsMinimized);

public static class CodexDesktopWindowMonitor
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GaRoot = 2;
    private const int DwmwaCloaked = 14;

    public static CodexDesktopWindowState Read()
    {
        var state = ReadForeground();
        return state.Provider == AgentProvider.Codex ? new(true, state.IsMinimized) : default;
    }

    public static DesktopWindowState ReadForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return default;
        var root = GetAncestor(foreground, GaRoot);
        if (root == IntPtr.Zero) root = foreground;
        GetWindowThreadProcessId(root, out var processId);
        var path = TryGetProcessPath(processId);
        return ObserveForeground(path, IsWindowVisible(root), TryIsCloaked(root), IsIconic(root));
    }

    public static DesktopWindowState ObserveForeground(string? path, bool isVisible, bool isCloaked, bool isMinimized) =>
        !isVisible || isCloaked ? default : IsCodexDesktopExecutable(path) ? new(AgentProvider.Codex, isMinimized)
            : IsClaudeDesktopExecutable(path) ? new(AgentProvider.Claude, isMinimized) : default;

    public static bool IsClaudeDesktopExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !string.Equals(Path.GetFileName(path), "Claude.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = path!.Replace('/', '\\');
        if (normalized.IndexOf("\\Claude\\claude-code\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return normalized.IndexOf("\\WindowsApps\\Claude_", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("\\AnthropicClaude\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void BringClaudeToFront()
    {
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || TryIsCloaked(window)) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (!IsClaudeDesktopExecutable(TryGetProcessPath(pid))) return true;
            if (IsIconic(window)) ShowWindow(window, 9);
            SetForegroundWindow(window);
            return false;
        }, IntPtr.Zero);
    }

    public static CodexDesktopWindowState Observe(string? path, bool isVisible, bool isCloaked, bool isMinimized) =>
        IsCodexDesktopExecutable(path) && isVisible && !isCloaked ? new(true, isMinimized) : default;

    private static bool TryIsCloaked(IntPtr window)
    {
        try
        {
            return DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, Marshal.SizeOf<int>()) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    public static bool IsCodexDesktopExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var executable = Path.GetFileName(path);
        if (!string.Equals(executable, "codex.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(executable, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var normalized = path!.Replace('/', '\\');
        return normalized.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("\\Programs\\Codex\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? TryGetProcessPath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024;
            var buffer = new System.Text.StringBuilder(capacity);
            return QueryFullProcessImageName(process, 0, buffer, ref capacity) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int valueSize);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
