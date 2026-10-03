namespace AqTracker.Core;

/// <summary>Carries user data from the pre-rename "CodexTracker" folders into the AqTracker folders.</summary>
public static class LegacyDataMigration
{
    public const string LegacyFolderName = "CodexTracker";
    public const string FolderName = "AqTracker";
    // Only settings.json proves the app already ran from the new folder; other files (for example a
    // quota history written by a dev or test run) must not block carrying the user's data over.
    private const string InUseMarker = "settings.json";

    public static void MigrateRoamingData() => MigrateDirectory(
        Path.Combine(UserFolders.ApplicationData, LegacyFolderName),
        Path.Combine(UserFolders.ApplicationData, FolderName));

    /// <summary>
    /// Moves <paramref name="legacyDirectory"/> into <paramref name="directory"/> unless the new folder is
    /// already in use. Legacy files replace stray files in a not-yet-used folder; when the legacy folder
    /// cannot be moved or removed (for example, a file is still locked) its files are copied instead.
    /// </summary>
    public static bool MigrateDirectory(string legacyDirectory, string directory)
    {
        if (!Directory.Exists(legacyDirectory) || File.Exists(Path.Combine(directory, InUseMarker))) return false;
        try
        {
            if (!Directory.Exists(directory))
            {
                try
                {
                    Directory.Move(legacyDirectory, directory);
                    return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            CopyInto(legacyDirectory, directory);
            try { Directory.Delete(legacyDirectory, true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SanitizedLogger.Write("Legacy data migration failed: " + error.GetType().Name);
            return false;
        }
    }

    private static void CopyInto(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.GetDirectories(source))
            CopyInto(child, Path.Combine(destination, Path.GetFileName(child)));
    }
}
