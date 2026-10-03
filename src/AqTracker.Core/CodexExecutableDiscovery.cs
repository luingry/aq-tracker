namespace AqTracker.Core;

public static class CodexExecutableDiscovery
{
    public static IEnumerable<string> Candidates(string? configuredPath, string? userProfile = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) yield return configuredPath!;
        foreach (var item in FindOnPath()) yield return item;
        var home = userProfile ?? UserFolders.UserProfile;
        yield return Path.Combine(home, ".codex", "plugins", ".plugin-appserver", "codex.exe");
        yield return Path.Combine(home, ".local", "bin", "codex.exe");
    }

    public static string? Find(string? configuredPath, string? userProfile = null) => Candidates(configuredPath, userProfile).FirstOrDefault(IsRunnableFile!);
    private static bool IsRunnableFile(string candidate) => File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> FindOnPath()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            var file = Path.Combine(path.Trim(), "codex.exe");
            if (File.Exists(file)) yield return file;
        }
    }
}
