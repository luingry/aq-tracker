namespace AqTracker.Core;

public sealed record UpdateAvailability(bool IsAvailable, string? LatestVersion, string? DownloadUrl);

/// <summary>Decides whether a GitHub release represents an installable update over the running version.</summary>
public static class UpdateEvaluator
{
    public const string VersionedInstallerAssetPrefix = "AqTracker-Setup-";
    public const string LegacyInstallerAssetName = "AqTracker-latest.exe";

    public static UpdateAvailability Evaluate(string currentVersion, GithubReleaseInfo? release)
    {
        if (release is null || release.Draft || release.Prerelease ||
            !SemanticVersion.TryParse(currentVersion, out var current) ||
            !SemanticVersion.TryParse(release.TagName, out var latest))
            return new(false, null, null);

        var asset = SelectInstallerAsset(release.Assets, latest);
        var isAvailable = asset is not null && latest.IsNewerThan(current);
        return new(isAvailable, latest.ToString(), isAvailable ? asset!.DownloadUrl : null);
    }

    public static GithubReleaseAsset? SelectInstallerAsset(IReadOnlyList<GithubReleaseAsset> assets, SemanticVersion releaseVersion)
    {
        var versionedAssetName = VersionedInstallerAssetPrefix + releaseVersion + ".exe";
        return assets.FirstOrDefault(asset => string.Equals(asset.Name, versionedAssetName, StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(asset => string.Equals(asset.Name, LegacyInstallerAssetName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Gates automatic update checks to at most once per rolling interval, persisted across runs.</summary>
public static class UpdateCheckPolicy
{
    public static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(24);

    public static bool IsDue(DateTimeOffset? lastCheckedUtc, DateTimeOffset nowUtc) =>
        lastCheckedUtc is null || nowUtc - lastCheckedUtc.Value >= MinimumCheckInterval;
}

/// <summary>Suppresses a deferred version only during the automatic-check cycle in which it was deferred.</summary>
public static class UpdateDeferralPolicy
{
    public static bool ShouldSuppressAutomaticPrompt(string? deferredVersion, DateTimeOffset? deferredAtUtc, string candidateVersion, DateTimeOffset? automaticCheckStartedUtc) =>
        deferredVersion is not null &&
        deferredAtUtc is not null &&
        automaticCheckStartedUtc is not null &&
        string.Equals(deferredVersion, candidateVersion, StringComparison.OrdinalIgnoreCase) &&
        deferredAtUtc.Value >= automaticCheckStartedUtc.Value;
}
