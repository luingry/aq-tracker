using System.Text.Json;

namespace AqTracker.Core;

public sealed record GithubReleaseAsset(string Name, string DownloadUrl);
public sealed record GithubReleaseInfo(string TagName, bool Prerelease, bool Draft, IReadOnlyList<GithubReleaseAsset> Assets);

public static class GithubReleaseParser
{
    public static GithubReleaseInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var tagName = root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
        if (string.IsNullOrWhiteSpace(tagName)) return null;

        var prerelease = root.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.ValueKind == JsonValueKind.True;
        var draft = root.TryGetProperty("draft", out var draftElement) && draftElement.ValueKind == JsonValueKind.True;

        var assets = new List<GithubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                var name = asset.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url)) assets.Add(new(name!, url!));
            }

        return new GithubReleaseInfo(tagName!, prerelease, draft, assets);
    }
}
