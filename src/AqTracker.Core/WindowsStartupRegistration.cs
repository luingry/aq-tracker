using Microsoft.Win32;

namespace AqTracker.Core;

/// <summary>Per-user Windows sign-in entry, shared by the installer and Settings.</summary>
public sealed class WindowsStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _executablePath, _keyPath;

    public WindowsStartupRegistration(string executablePath, string keyPath = RunKey)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _keyPath = keyPath;
    }

    public string Command => "\"" + _executablePath + "\" --startup";
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue("AqTracker") is string current && !string.IsNullOrWhiteSpace(current) ||
                key?.GetValue("CodexTracker") is string legacy && !string.IsNullOrWhiteSpace(legacy);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        if (enabled) key.SetValue("AqTracker", Command, RegistryValueKind.String);
        else key.DeleteValue("AqTracker", throwOnMissingValue: false);
        key.DeleteValue("CodexTracker", throwOnMissingValue: false);
    }
}
