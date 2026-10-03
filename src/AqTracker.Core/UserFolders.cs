namespace AqTracker.Core;

/// <summary>
/// Resolves per-user folders without ever returning an empty path. At Windows sign-in a
/// Run-key process can start before the shell verifies the profile folders; the default
/// GetFolderPath then returns "" and every data path silently becomes relative to
/// System32, so settings, Claude credentials and logs appear missing until a restart.
/// </summary>
public static class UserFolders
{
    public static string ApplicationData => Resolve(Environment.SpecialFolder.ApplicationData, "APPDATA", Path.Combine("AppData", "Roaming"));
    public static string LocalApplicationData => Resolve(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA", Path.Combine("AppData", "Local"));
    public static string UserProfile => Resolve(Environment.SpecialFolder.UserProfile, "USERPROFILE", null);

    public static string Resolve(Environment.SpecialFolder folder, string environmentVariable, string? profileRelativePath,
        Func<Environment.SpecialFolder, string>? specialFolder = null, Func<string, string?>? environment = null)
    {
        specialFolder ??= value => Environment.GetFolderPath(value, Environment.SpecialFolderOption.DoNotVerify);
        environment ??= Environment.GetEnvironmentVariable;
        var path = specialFolder(folder);
        if (IsUsable(path)) return path;
        path = environment(environmentVariable) ?? "";
        if (IsUsable(path)) return path;
        var profile = specialFolder(Environment.SpecialFolder.UserProfile);
        if (!IsUsable(profile)) profile = environment("USERPROFILE") ?? "";
        if (!IsUsable(profile))
        {
            var drive = environment("HOMEDRIVE");
            var home = environment("HOMEPATH");
            profile = drive is not null && home is not null ? drive + home : "";
        }
        if (!IsUsable(profile)) throw new InvalidOperationException("User profile folder is unavailable.");
        return profileRelativePath is null ? profile : Path.Combine(profile, profileRelativePath);
    }

    private static bool IsUsable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);
}
