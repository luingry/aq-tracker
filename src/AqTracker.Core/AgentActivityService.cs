using System.Globalization;
using System.Text.Json;

namespace AqTracker.Core;

public sealed record ActiveAgent(
    string ThreadId,
    string? ParentThreadId,
    int HierarchyDepth,
    bool IsSubagent,
    string Type,
    string Title,
    string Status,
    string Model,
    string Effort,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    string? ProjectPath = null,
    AgentProvider Provider = AgentProvider.Codex,
    string? HostSessionId = null);

public sealed record CompletedAgentWork(
    string CompletionId,
    string ThreadId,
    string Type,
    string Title,
    string Status,
    string Model,
    string Effort,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? ProjectPath = null,
    AgentProvider Provider = AgentProvider.Codex,
    string? HostSessionId = null);

public sealed record AgentActivitySnapshot(
    IReadOnlyList<ActiveAgent> ActiveAgents,
    IReadOnlyList<CompletedAgentWork> CompletedAgentWorks);

/// <summary>
/// Reconstructs live Codex turns from the append-only local rollout stream. A turn is
/// considered active only while its task_started marker has no matching task_complete
/// marker and the file is still receiving activity.
/// </summary>
public sealed class AgentActivityService
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(5);
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _staleAfter;
    private readonly Dictionary<string, CachedRollout> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProjectRootResolver _projectRoots = new();

    public AgentActivityService(Func<DateTimeOffset>? now = null, TimeSpan? staleAfter = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? DefaultStaleAfter;
    }

    public IReadOnlyList<ActiveAgent> Read(IReadOnlyDictionary<string, string>? titles = null, string? sessionsRoot = null)
        => ReadSnapshot(titles, sessionsRoot).ActiveAgents;

    public AgentActivitySnapshot ReadSnapshot(IReadOnlyDictionary<string, string>? titles = null, string? sessionsRoot = null)
    {
        var now = _now().ToUniversalTime();
        var root = sessionsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
        if (!Directory.Exists(root)) return new([], []);

        var cutoff = now - _staleAfter;
        var recentFiles = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists &&
                           (file.LastWriteTimeUtc >= cutoff.UtcDateTime ||
                            IsRecentRolloutCandidate(file, now) ||
                            ShouldRetainCachedFile(file, cutoff)))
            .ToArray();
        var recentPaths = new HashSet<string>(recentFiles.Select(file => file.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var stalePath in _cache.Keys.Where(path => !recentPaths.Contains(path)).ToArray()) _cache.Remove(stalePath);

        foreach (var file in recentFiles)
        {
            try
            {
                var signature = new RolloutSignature(file.Length, file.LastWriteTimeUtc.Ticks);
                if (_cache.TryGetValue(file.FullName, out var cached) && cached.Signature == signature) continue;

                var state = cached is not null && signature.Length > cached.Signature.Length && cached.HadFinalNewline
                    ? Parse(file.FullName, cached.Signature.Length, cached.State)
                    : Parse(file.FullName, 0, RolloutState.Empty);
                _cache[file.FullName] = new(signature, HasFinalNewline(file.FullName, signature.Length), state);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var activeStates = _cache.Values
            .Select(value => value.State)
            .Where(state => !state.IsMemorySession && state.ActiveTurnId is not null && state.StartedAt is not null && state.LastActivityAt >= cutoff)
            .GroupBy(state => state.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.LastActivityAt).First())
            .ToArray();

        var activeStatesByThread = activeStates.ToDictionary(state => state.ThreadId, StringComparer.OrdinalIgnoreCase);
        activeStates = activeStates.Select(state => ResolveProjectPath(state, activeStatesByThread)).ToArray();
        var activeAgents = OrderHierarchy(activeStates)
            .Select(ordered => ToActiveAgent(ordered.State, ordered.Depth, titles))
            .ToArray();
        var completedAgents = _cache.Values
            .Select(value => value.State)
            .Where(state => !state.IsMemorySession && !state.IsSubagent && state.CompletedAt is not null && !string.IsNullOrWhiteSpace(state.CompletedTurnId))
            .GroupBy(state => state.ThreadId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(state => state.CompletedAt).First())
            .OrderByDescending(state => state.CompletedAt)
            .Select(state => ToCompletedAgentWork(state, titles))
            .ToArray();
        return new(activeAgents, completedAgents);
    }

    private static IReadOnlyList<OrderedState> OrderHierarchy(IReadOnlyList<RolloutState> states)
    {
        var orderedNodes = states.OrderBy(state => state.StartedAt).ThenBy(state => state.ThreadId, StringComparer.OrdinalIgnoreCase).ToArray();
        var nodesById = orderedNodes.ToDictionary(state => state.ThreadId, StringComparer.OrdinalIgnoreCase);
        var childrenByParent = orderedNodes
            .Where(state => !string.IsNullOrWhiteSpace(state.ParentThreadId) &&
                            !string.Equals(state.ThreadId, state.ParentThreadId, StringComparison.OrdinalIgnoreCase) &&
                            nodesById.ContainsKey(state.ParentThreadId!))
            .GroupBy(state => state.ParentThreadId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(state => state.StartedAt).ThenBy(state => state.ThreadId, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OrderedState>(orderedNodes.Length);

        void Visit(RolloutState state, int depth)
        {
            if (!seen.Add(state.ThreadId)) return;
            result.Add(new(state, depth));
            if (!childrenByParent.TryGetValue(state.ThreadId, out var children)) return;
            foreach (var child in children) Visit(child, depth + 1);
        }

        foreach (var root in orderedNodes.Where(state => string.IsNullOrWhiteSpace(state.ParentThreadId) ||
                                                         string.Equals(state.ThreadId, state.ParentThreadId, StringComparison.OrdinalIgnoreCase) ||
                                                         !nodesById.ContainsKey(state.ParentThreadId!)))
            Visit(root, 0);
        foreach (var remaining in orderedNodes) Visit(remaining, 0);
        return result;
    }

    private static RolloutState ResolveProjectPath(RolloutState state, IReadOnlyDictionary<string, RolloutState> statesByThread)
    {
        if (!string.IsNullOrWhiteSpace(state.ProjectPath)) return state;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state.ThreadId };
        var parentThreadId = state.ParentThreadId;
        while (!string.IsNullOrWhiteSpace(parentThreadId) && visited.Add(parentThreadId!) && statesByThread.TryGetValue(parentThreadId!, out var parent))
        {
            if (!string.IsNullOrWhiteSpace(parent.ProjectPath)) return state with { ProjectPath = parent.ProjectPath };
            parentThreadId = parent.ParentThreadId;
        }
        return state;
    }

    private static ActiveAgent ToActiveAgent(RolloutState state, int hierarchyDepth, IReadOnlyDictionary<string, string>? titles)
    {
        var title = titles is not null && titles.TryGetValue(state.ThreadId, out var mappedTitle) && !string.IsNullOrWhiteSpace(mappedTitle)
            ? mappedTitle.Trim()
            : FallbackTitle(state);
        return new(state.ThreadId, state.ParentThreadId, hierarchyDepth, state.IsSubagent, state.IsSubagent ? "Subagent" : "Agent", title,
            string.IsNullOrWhiteSpace(state.Status) ? "Trabalhando" : state.Status,
            string.IsNullOrWhiteSpace(state.Model) ? "unknown" : state.Model,
            string.IsNullOrWhiteSpace(state.Effort) ? "unknown" : state.Effort,
            state.StartedAt!.Value, state.LastActivityAt, state.ProjectPath);
    }

    private static string FallbackTitle(RolloutState state)
    {
        if (!string.IsNullOrWhiteSpace(state.AgentPath))
        {
            var segment = state.AgentPath!.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(segment)) return segment.Replace('_', ' ');
        }
        if (!string.IsNullOrWhiteSpace(state.AgentNickname)) return state.AgentNickname!;
        return state.IsSubagent ? "Subagent Codex" : "Conversa Codex";
    }

    private static CompletedAgentWork ToCompletedAgentWork(RolloutState state, IReadOnlyDictionary<string, string>? titles)
    {
        var title = titles is not null && titles.TryGetValue(state.ThreadId, out var mappedTitle) && !string.IsNullOrWhiteSpace(mappedTitle)
            ? mappedTitle.Trim()
            : FallbackTitle(state);
        return new(
            state.ThreadId + ":" + state.CompletedTurnId,
            state.ThreadId,
            "Agent",
            title,
            "Concluído",
            string.IsNullOrWhiteSpace(state.Model) ? "unknown" : state.Model,
            string.IsNullOrWhiteSpace(state.Effort) ? "unknown" : state.Effort,
            state.CompletedStartedAt ?? state.CompletedAt!.Value,
            state.CompletedAt!.Value,
            state.ProjectPath);
    }

    private RolloutState Parse(string path, long offset, RolloutState initial)
    {
        var state = initial;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var at = ReadTimestamp(root) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                var topType = ReadString(root, "type");

                if (topType == "session_meta" && string.IsNullOrWhiteSpace(state.ThreadId))
                {
                    var threadId = ReadString(payload, "id") ?? ReadString(payload, "session_id") ?? path;
                    var source = ReadString(payload, "thread_source");
                    var parent = ReadString(payload, "parent_thread_id") ?? ReadString(payload, "forked_from_id");
                    var cwd = ReadString(payload, "cwd");
                    state = state with { ThreadId = threadId, ParentThreadId = parent, IsSubagent = source == "subagent" || !string.IsNullOrWhiteSpace(parent), AgentPath = ReadString(payload, "agent_path"), AgentNickname = ReadString(payload, "agent_nickname"), ProjectPath = _projectRoots.Resolve(cwd), IsMemorySession = IsMemoryPath(cwd) };
                    continue;
                }

                if (topType == "turn_context" || ReadString(payload, "type") == "turn_context")
                {
                    state = state with { Model = ReadString(payload, "model") ?? state.Model, Effort = ReadString(payload, "effort") ?? state.Effort, LastActivityAt = at };
                    continue;
                }

                if (topType != "event_msg" && topType != "response_item") continue;
                state = state with { LastActivityAt = at };
                if (topType != "event_msg") continue;
                switch (ReadString(payload, "type"))
                {
                    case "task_started":
                        state = state with { ActiveTurnId = ReadString(payload, "turn_id") ?? at.ToUnixTimeMilliseconds().ToString(), StartedAt = at, Status = "Trabalhando" };
                        break;
                    case "task_complete":
                        var completedTurn = ReadString(payload, "turn_id");
                        if (string.IsNullOrWhiteSpace(completedTurn) || completedTurn == state.ActiveTurnId)
                            state = state with
                            {
                                CompletedTurnId = completedTurn ?? state.ActiveTurnId ?? at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                                CompletedStartedAt = state.StartedAt,
                                CompletedAt = at,
                                ActiveTurnId = null
                            };
                        break;
                    case "agent_reasoning":
                        if (state.ActiveTurnId is not null && ReadString(payload, "text") is { } reasoning && !string.IsNullOrWhiteSpace(reasoning)) state = state with { Status = PresentStatus(reasoning) };
                        break;
                }
            }
            catch (JsonException) { }
        }
        return state;
    }

    private static string PresentStatus(string value)
    {
        var line = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "Trabalhando";
        line = line.TrimStart('#', '-', '*', ' ').Replace("`", "");
        return line.Length <= 110 ? line : line.Substring(0, 107).TrimEnd() + "...";
    }

    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? ReadTimestamp(JsonElement element) => ReadString(element, "timestamp") is { } value && DateTimeOffset.TryParse(value, out var timestamp) ? timestamp.ToUniversalTime() : null;

    private static bool IsMemoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var memoriesRoot = NormalizePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "memories"));
            var candidate = NormalizePath(Environment.ExpandEnvironmentVariables(path));
            if (string.IsNullOrEmpty(memoriesRoot) || string.IsNullOrEmpty(candidate)) return false;
            return string.Equals(candidate, memoriesRoot, StringComparison.OrdinalIgnoreCase) ||
                   (candidate.Length > memoriesRoot.Length && candidate.StartsWith(memoriesRoot, StringComparison.OrdinalIgnoreCase) && IsDirectorySeparator(candidate[memoriesRoot.Length]));
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsDirectorySeparator(char value) => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    private bool ShouldRetainCachedFile(FileInfo file, DateTimeOffset cutoff)
    {
        if (!_cache.TryGetValue(file.FullName, out var cached)) return false;
        var signature = new RolloutSignature(file.Length, file.LastWriteTimeUtc.Ticks);
        return cached.State.LastActivityAt >= cutoff || cached.Signature != signature;
    }

    private static bool IsRecentRolloutCandidate(FileInfo file, DateTimeOffset now)
    {
        const string prefix = "rollout-";
        var name = file.Name;
        var localToday = now.LocalDateTime.Date;
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length >= prefix.Length + 10 &&
               DateTime.TryParseExact(name.Substring(prefix.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sessionDate) &&
               (sessionDate.Date == localToday || sessionDate.Date == localToday.AddDays(-1));
    }
    private static bool HasFinalNewline(string path, long length)
    {
        if (length == 0) return true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() is '\n' or '\r';
    }

    private sealed record CachedRollout(RolloutSignature Signature, bool HadFinalNewline, RolloutState State);
    private readonly record struct RolloutSignature(long Length, long LastWriteUtcTicks);
    private sealed record OrderedState(RolloutState State, int Depth);
    private sealed record RolloutState(string ThreadId, string? ParentThreadId, bool IsSubagent, string? AgentPath, string? AgentNickname, string? ProjectPath, bool IsMemorySession, string? ActiveTurnId, DateTimeOffset? StartedAt, string? CompletedTurnId, DateTimeOffset? CompletedStartedAt, DateTimeOffset? CompletedAt, DateTimeOffset LastActivityAt, string Model, string Effort, string Status)
    {
        public static RolloutState Empty { get; } = new("", null, false, null, null, null, false, null, null, null, null, null, DateTimeOffset.MinValue, "unknown", "unknown", "Trabalhando");
    }
}
