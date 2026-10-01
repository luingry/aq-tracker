namespace AqTracker.Core;

public static class CodexThreadDeepLink
{
    public static bool TryCreate(string? threadId, out Uri? deepLink)
    {
        deepLink = null;
        if (!Guid.TryParseExact(threadId, "D", out var id)) return false;

        deepLink = new Uri($"codex://threads/{id:D}", UriKind.Absolute);
        return true;
    }
}
