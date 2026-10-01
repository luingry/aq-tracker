using System.Diagnostics;
using System.IO;
using AqTracker.Core;

namespace AqTracker;

/// <summary>Checks GitHub for a newer release and drives the transparent download + silent install flow.</summary>
public sealed class UpdateController : IDisposable
{
    private readonly GithubUpdateClient _client;

    public UpdateController(GithubUpdateClient? client = null) => _client = client ?? new GithubUpdateClient();

    public async Task<UpdateAvailability> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var release = await _client.FetchLatestReleaseAsync(cancellationToken);
        return UpdateEvaluator.Evaluate(currentVersion, release);
    }

    public async Task<string> DownloadAsync(string url, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AqTracker", "updates");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"AqTracker-Update-{Guid.NewGuid():N}.exe");
        try
        {
            await _client.DownloadAsync(url, destination, progress, cancellationToken);
            if (!File.Exists(destination) || new FileInfo(destination).Length <= 0)
                throw new InvalidOperationException("Downloaded update installer is empty.");
            return destination;
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    // /update=1 is a custom Inno Setup parameter (see installer/AqTracker.iss) that forces the
    // installer to relaunch the app after a silent install; [Run]'s skipifsilent flag would otherwise
    // suppress that for every unattended install, including this in-app update flow.
    public void StartSilentInstall(string installerPath)
    {
        var logDirectory = Path.GetDirectoryName(SanitizedLogger.LogPath)!;
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "update-install.log");
        var arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{logPath}\" /update=1";
        var process = Process.Start(new ProcessStartInfo(installerPath, arguments) { UseShellExecute = true });
        if (process is null) throw new InvalidOperationException("Failed to start the update installer.");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public void Dispose() => _client.Dispose();
}
