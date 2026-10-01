namespace CodexTracker.Core;

public static class WidgetVisibilityPolicy
{
    public static bool ShouldShow(ProfileActivity codex, ProfileActivity claude, bool isWidgetActive) =>
        codex.IsEngaged || claude.IsEngaged || isWidgetActive;

    public static bool ShouldShow(bool hasActiveWork, bool hasUnreadCompletedWork, bool isCodexForeground, bool isCodexMinimized, bool isWidgetActive) =>
        hasActiveWork || hasUnreadCompletedWork || isWidgetActive || isCodexForeground && !isCodexMinimized;
}
