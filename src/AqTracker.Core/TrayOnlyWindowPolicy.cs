namespace AqTracker.Core;

public static class TrayOnlyWindowPolicy
{
    public const long ToolWindowExtendedStyle = 0x00000080L;
    public const long AppWindowExtendedStyle = 0x00040000L;

    public static long ToTrayOnlyExtendedStyle(long extendedStyle) =>
        (extendedStyle | ToolWindowExtendedStyle) & ~AppWindowExtendedStyle;
}
