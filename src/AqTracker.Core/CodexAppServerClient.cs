using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AqTracker.Core;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly string _executable;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
    private Process? _process;
    private ChildProcessJob? _job;
    private CancellationTokenSource? _lifetime;
    private long _nextId;
    private int _terminated;
    public event Action<RateLimitSnapshot>? SnapshotUpdated;
    public event Action<string>? StatusChanged;
    public RateLimitSnapshot? Snapshot { get; private set; }

    public CodexAppServerClient(string executable) => _executable = executable;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _process = new Process { StartInfo = new ProcessStartInfo(_executable, "app-server --listen stdio://") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => TerminatePending(new InvalidOperationException("Codex app-server exited."));
        _process.Start();
        _job?.Dispose();
        _job = TryCreateJobFor(_process);
        _ = Task.Run(() => ReadLoopAsync(_lifetime.Token));
        _ = Task.Run(() => DrainErrorAsync(_lifetime.Token));
        StatusChanged?.Invoke("Connecting to Codex");
        await RequestAsync("initialize", new { clientInfo = new { name = "aq-tracker", version = "0.3.0" }, capabilities = new { } }, cancellationToken);
        await NotifyAsync("initialized", new { }, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("account/rateLimits/read", null, cancellationToken);
        ApplySnapshot(RateLimitParser.Parse(result, DateTimeOffset.UtcNow), false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadThreadTitlesAsync(IEnumerable<string> threadIds, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var threadId in threadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var result = await RequestAsync("thread/read", new { threadId, includeTurns = false }, cancellationToken);
                if (!result.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object) continue;
                if (thread.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                    titles[threadId] = name.GetString()!;
            }
            catch (InvalidOperationException) { }
        }
        return titles;
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = source;
        await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime?.Token ?? CancellationToken.None);
        timeout.CancelAfter(RequestTimeout);
        using var registration = timeout.Token.Register(() => source.TrySetException(new TimeoutException($"Timed out waiting for Codex {method}.")));
        try { return await source.Task; }
        finally { lock (_pending) _pending.Remove(id); }
    }

    private Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_process is null) throw new InvalidOperationException("Codex app-server is not running.");
        await _writeLock.WaitAsync(cancellationToken);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload)); await _process.StandardInput.FlushAsync(); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_process is { HasExited: false } && !cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(_process.StandardOutput, cancellationToken);
                if (line is null || string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var idNode) && idNode.TryGetInt64(out var id))
                {
                    TaskCompletionSource<JsonElement>? source;
                    lock (_pending) { _pending.TryGetValue(id, out source); _pending.Remove(id); }
                    if (source is null) continue;
                    if (message.TryGetProperty("error", out var error)) source.TrySetException(new InvalidOperationException("Codex rejected the request: " + error.GetRawText()));
                    else if (message.TryGetProperty("result", out var result)) source.TrySetResult(result.Clone());
                    continue;
                }
                if (message.TryGetProperty("method", out var method) && method.GetString() == "account/rateLimits/updated" && message.TryGetProperty("params", out var update))
                    ApplySnapshot(RateLimitParser.Parse(update, DateTimeOffset.UtcNow), true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TerminatePending(ex); StatusChanged?.Invoke("Connection interrupted: " + ex.Message); }
        finally { if (!cancellationToken.IsCancellationRequested) TerminatePending(new InvalidOperationException("Codex app-server output closed.")); }
    }

    private async Task DrainErrorAsync(CancellationToken cancellationToken)
    {
        try { while (_process is { HasExited: false } && !cancellationToken.IsCancellationRequested) _ = await ReadLineAsync(_process.StandardError, cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private void ApplySnapshot(RateLimitSnapshot next, bool sparse)
    {
        Snapshot = sparse && Snapshot is not null ? RateLimitParser.Merge(Snapshot, next) : next;
        StatusChanged?.Invoke("Live from Codex");
        SnapshotUpdated?.Invoke(Snapshot);
    }
    private void TerminatePending(Exception exception)
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 1) return;
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_pending) { pending = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var request in pending) request.TrySetException(exception);
        StatusChanged?.Invoke("Connection interrupted: " + exception.Message);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null) _lifetime.Cancel();
        TerminatePending(new OperationCanceledException("Agent Quota Tracker is closing."));
        // The job also ends descendants that outlived an already-exited app-server.
        if (_job is not null) _job.Dispose();
        else if (_process is { HasExited: false }) TerminateProcessTree(_process);
        _process?.Dispose(); _lifetime?.Dispose(); _writeLock.Dispose();
    }

    private static ChildProcessJob? TryCreateJobFor(Process process)
    {
        ChildProcessJob? job = null;
        try
        {
            job = ChildProcessJob.Create();
            job.Assign(process);
            return job;
        }
        catch (Exception ex)
        {
            job?.Dispose();
            SanitizedLogger.Write("Codex app-server job object unavailable, taskkill fallback on close: " + ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + ")");
            return null;
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var read = reader.ReadLineAsync();
        var cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
        if (await Task.WhenAny(read, cancelled) != read) throw new OperationCanceledException(cancellationToken);
        return await read;
    }

    // Fallback only when the job object could not be created or assigned.
    // Process.Kill(entireProcessTree: true) is unavailable on .NET Framework.
    // taskkill is part of supported Windows and retains the former tree-termination contract.
    private static void TerminateProcessTree(Process process)
    {
        try
        {
            using (var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true }))
            {
                taskkill?.WaitForExit(5000);
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
        }
    }
}
