using System.Diagnostics;
using System.Text.Json;

namespace AqTracker.Core;

public sealed record ClaudeSession(string SessionId, int Pid, string? Cwd, string? Entrypoint, string Title,
    string Status, string Model, string Effort, DateTimeOffset UpdatedAt, DateTimeOffset StatusUpdatedAt, DateTimeOffset? LastFocusedAt,
    string? HostSessionId = null);

/// <summary>Reads only live interactive registrations and the optional desktop metadata, never credentials.</summary>
public sealed class ClaudeSessionActivityService
{
    private readonly string _sessionsRoot, _metadataRoot;
    private readonly Func<int, bool> _isAlive;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ProjectRootResolver _projects = new();
    private readonly Dictionary<string, (long Mtime, long Length, DesktopMetadata? Metadata)> _metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ClaudeSession> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _started = new(StringComparer.Ordinal);
    public int MetadataFilesParsedLastRead { get; private set; }
    public IReadOnlyDictionary<string, DateTimeOffset> LastFocusedAt { get; private set; } = new Dictionary<string, DateTimeOffset>();

    public ClaudeSessionActivityService(string? sessionsRoot = null, string? metadataRoot = null,
        Func<int, bool>? isAlive = null, Func<DateTimeOffset>? clock = null)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        _metadataRoot = metadataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");
        _isAlive = isAlive ?? IsProcessAlive;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<ClaudeSession> ReadSessions()
    {
        var metadata = ReadMetadata();
        LastFocusedAt = metadata.Values.Where(item => item.LastFocusedAt.HasValue)
            .ToDictionary(item => item.SessionId, item => item.LastFocusedAt!.Value, StringComparer.Ordinal);
        var result = new Dictionary<string, ClaudeSession>(StringComparer.Ordinal);
        foreach (var path in Enumerate(_sessionsRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = ReadJson(path);
                var root = document.RootElement;
                var id = Text(root, "sessionId");
                if (Text(root, "kind") != "interactive" || string.IsNullOrWhiteSpace(id) ||
                    !root.TryGetProperty("pid", out var pidNode) || !pidNode.TryGetInt32(out var pid) || pid <= 0 || !_isAlive(pid)) continue;
                metadata.TryGetValue(id!, out var desktop);
                var cwd = Text(root, "cwd");
                var title = Text(root, "name") ?? desktop?.Title ?? CwdTitle(cwd) ?? "Claude";
                var now = _clock();
                // Validate the raw value: trimming would accept ids rejected by Claude.
                var hostSessionId = root.TryGetProperty("hostSessionId", out var hostNode) && hostNode.ValueKind == JsonValueKind.String
                    ? hostNode.GetString() : null;
                if (!ClaudeSessionDeepLink.IsValidHostSessionId(hostSessionId)) hostSessionId = null;
                var session = new ClaudeSession(id!, pid, cwd, Text(root, "entrypoint"), title,
                    Text(root, "status") ?? "unknown", desktop?.Model ?? "unknown", desktop?.Effort ?? "unknown",
                    Epoch(root, "updatedAt") ?? now, Epoch(root, "statusUpdatedAt") ?? now, desktop?.LastFocusedAt, hostSessionId);
                if (!result.TryGetValue(id!, out var existing) || session.UpdatedAt > existing.UpdatedAt) result[id!] = session;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException) { }
        }
        return result.Values.ToArray();
    }

    public AgentActivitySnapshot ReadSnapshot()
    {
        var sessions = ReadSessions();
        var active = new List<ActiveAgent>();
        var completed = new List<CompletedAgentWork>();
        foreach (var session in sessions)
        {
            if (session.Status == "busy")
            {
                if (!_previous.TryGetValue(session.SessionId, out var previous) || previous.Status != "busy")
                    _started[session.SessionId] = session.StatusUpdatedAt;
                active.Add(new(session.SessionId, null, 0, false, "Claude", session.Title, "Working", session.Model, session.Effort,
                    _started[session.SessionId], session.UpdatedAt, _projects.Resolve(session.Cwd), AgentProvider.Claude, session.HostSessionId));
            }
            else if (_previous.TryGetValue(session.SessionId, out var previous) && previous.Status == "busy")
            {
                // Timestamp supplied by the registration, falling back to the observation clock.
                var at = session.StatusUpdatedAt;
                var start = _started[session.SessionId];
                if (at < start) at = _clock();
                completed.Add(new("claude:" + session.SessionId + ":" + at.ToUnixTimeMilliseconds(), session.SessionId,
                    "Claude", session.Title, "Completed", session.Model, session.Effort, start, at,
                    _projects.Resolve(session.Cwd), AgentProvider.Claude, session.HostSessionId));
            }
        }
        // Disappearance is not a completion and cannot be carried into a later idle registration.
        _previous = sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        foreach (var id in _started.Keys.Where(id => !_previous.ContainsKey(id)).ToArray()) _started.Remove(id);
        return new(active, completed);
    }

    public static bool ShouldRemoveCompletion(CompletedAgentWork work, IEnumerable<ActiveAgent> active,
        IReadOnlyDictionary<string, DateTimeOffset> focused) => work.Provider == AgentProvider.Claude &&
        (active.Any(agent => agent.Provider == AgentProvider.Claude && agent.ThreadId == work.ThreadId) ||
         focused.TryGetValue(work.ThreadId, out var at) && at > work.CompletedAt);

    private Dictionary<string, DesktopMetadata> ReadMetadata()
    {
        MetadataFilesParsedLastRead = 0;
        var result = new Dictionary<string, DesktopMetadata>(StringComparer.Ordinal);
        var paths = Enumerate(_metadataRoot, "local_*.json", SearchOption.AllDirectories);
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!_metadataCache.TryGetValue(path, out var cached) || cached.Mtime != info.LastWriteTimeUtc.Ticks || cached.Length != info.Length)
                {
                    using var document = ReadJson(path);
                    var root = document.RootElement;
                    var id = Text(root, "cliSessionId");
                    var item = id is null ? null : new DesktopMetadata(id, Text(root, "title"), Text(root, "model"), Text(root, "effort"), Epoch(root, "lastFocusedAt"));
                    cached = (info.LastWriteTimeUtc.Ticks, info.Length, item);
                    _metadataCache[path] = cached;
                    MetadataFilesParsedLastRead++;
                }
                if (cached.Metadata is { } metadata && (!result.TryGetValue(metadata.SessionId, out var previous) ||
                    (metadata.LastFocusedAt ?? DateTimeOffset.MinValue) > (previous.LastFocusedAt ?? DateTimeOffset.MinValue))) result[metadata.SessionId] = metadata;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _metadataCache.Keys.Where(path => !present.Contains(path)).ToArray()) _metadataCache.Remove(path);
        return result;
    }

    private static string[] Enumerate(string root, string pattern, SearchOption option)
    {
        try { return Directory.Exists(root) ? Directory.GetFiles(root, pattern, option) : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
    private static JsonDocument ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }
    private static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var node) &&
        node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString()) ? node.GetString()!.Trim() : null;
    private static DateTimeOffset? Epoch(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Number || !node.TryGetInt64(out var value)) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value); } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static string? CwdTitle(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var trimmed = cwd!.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        var name = trimmed.Substring(index + 1);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
    private static bool IsProcessAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch { return false; } }
    private sealed record DesktopMetadata(string SessionId, string? Title, string? Model, string? Effort, DateTimeOffset? LastFocusedAt);
}
