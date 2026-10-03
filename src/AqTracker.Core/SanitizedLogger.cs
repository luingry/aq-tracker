namespace AqTracker.Core;
public static class SanitizedLogger
{
    public static readonly string LogPath = Path.Combine(UserFolders.LocalApplicationData, "AqTracker", "logs", "aq-tracker.log");
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512_000) { var archive = LogPath + ".1"; if (File.Exists(archive)) File.Delete(archive); File.Move(LogPath, archive); }
            var home = UserFolders.UserProfile;
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {ReplaceIgnoreCase(message, home, "%USERPROFILE%").Replace(Environment.NewLine, " ")}\n");
        }
        catch { }
    }
    private static string ReplaceIgnoreCase(string value, string oldValue, string newValue) => System.Text.RegularExpressions.Regex.Replace(value, System.Text.RegularExpressions.Regex.Escape(oldValue), newValue.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
