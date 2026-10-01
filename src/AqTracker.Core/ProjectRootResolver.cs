namespace AqTracker.Core;

/// <summary>Resolves only locally verifiable Git worktree roots without invoking Git.</summary>
public sealed class ProjectRootResolver
{
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Resolve(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(cwd); }
        catch { return null; }
        if (_cache.TryGetValue(fullPath, out var cached)) return cached;
        var result = ResolveUncached(fullPath);
        _cache[fullPath] = result;
        return result;
    }

    private static string? ResolveUncached(string fullPath)
    {
        if (!Directory.Exists(fullPath)) return null;
        for (var directory = new DirectoryInfo(fullPath); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker)) return directory.FullName;
            if (!File.Exists(marker)) continue;
            var gitDirectory = ReadGitDir(marker, directory.FullName);
            if (gitDirectory is null || !Directory.Exists(gitDirectory)) return null;
            var commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile))
            {
                var commonDirectory = ReadCommonDir(gitDirectory);
                if (commonDirectory is null || !Directory.Exists(commonDirectory) || !string.Equals(Path.GetFileName(commonDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), ".git", StringComparison.OrdinalIgnoreCase)) return null;
                return Directory.GetParent(commonDirectory)?.FullName;
            }
            return directory.FullName;
        }
        return null;
    }

    private static string? ReadGitDir(string marker, string ancestor)
    {
        try
        {
            var line = File.ReadLines(marker).FirstOrDefault();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            var value = line.Substring("gitdir:".Length).Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ancestor, value));
        }
        catch { return null; }
    }

    private static string? ReadCommonDir(string gitDirectory)
    {
        var commonFile = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(commonFile)) return null;
        try
        {
            var value = File.ReadLines(commonFile).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(gitDirectory, value));
        }
        catch { return null; }
    }
}
