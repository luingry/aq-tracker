namespace AqTracker.Core;

public static class WidgetVisibilityPolicy
{
    public static bool ShouldShow(ProfileActivity codex, ProfileActivity claude, bool isWidgetActive) =>
        codex.IsEngaged || claude.IsEngaged || isWidgetActive;

    public static bool ShouldShow(bool hasActiveWork, bool hasUnreadCompletedWork, bool isCodexForeground, bool isCodexMinimized, bool isWidgetActive) =>
        hasActiveWork || hasUnreadCompletedWork || isWidgetActive || isCodexForeground && !isCodexMinimized;
}

/// <summary>
/// Keeps a widget started hidden at sign-in out of the way for activity that already
/// existed then, and hands control back to normal visibility on the first change.
/// Waiting for a full idle -> engaged cycle left the widget hidden indefinitely when
/// Codex or Claude started with work already present.
/// </summary>
public sealed class StartupVisibilityGate
{
    private (ProfileActivity Codex, ProfileActivity Claude)? _baseline;

    /// <summary>Returns true once activity differs from the first observed snapshot.</summary>
    public bool Release(ProfileActivity codex, ProfileActivity claude)
    {
        if (_baseline is null) { _baseline = (codex, claude); return false; }
        return _baseline.Value != (codex, claude);
    }
}
