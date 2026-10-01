using System.Text.RegularExpressions;

namespace AqTracker.Core;

public static class ClaudeSessionDeepLink
{
    private static readonly Regex HostSessionIdPattern = new(@"\Alocal_[A-Za-z0-9-]{1,64}\z", RegexOptions.CultureInvariant);

    public static bool IsValidHostSessionId(string? hostSessionId) => hostSessionId is not null &&
        hostSessionId.Length <= 70 && HostSessionIdPattern.IsMatch(hostSessionId);

    public static bool TryCreate(string? hostSessionId, out Uri? deepLink)
    {
        deepLink = null;
        if (!IsValidHostSessionId(hostSessionId)) return false;

        deepLink = new Uri($"claude://code/continue?session={hostSessionId}&source=url_external", UriKind.Absolute);
        return true;
    }
}
