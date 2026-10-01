using System.Net.Http;

namespace AqTracker.Core;

/// <summary>Fetches the latest public release metadata and downloads its installer asset over plain HTTPS.</summary>
public sealed class GithubUpdateClient : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/luingry/aq-tracker/releases/latest";
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public GithubUpdateClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AqTracker-Updater");
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<GithubReleaseInfo?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        return GithubReleaseParser.Parse(json);
    }

    public async Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        using var source = await response.Content.ReadAsStreamAsync();
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long readBytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer, 0, read, cancellationToken);
            readBytes += read;
            if (totalBytes is > 0) progress?.Report(Math.Min(1d, readBytes / (double)totalBytes.Value));
        }
        if (totalBytes is { } expected && readBytes != expected)
            throw new IOException($"Update download ended after {readBytes} of {expected} expected bytes.");
    }

    public void Dispose() { if (_ownsHttpClient) _http.Dispose(); }
}
