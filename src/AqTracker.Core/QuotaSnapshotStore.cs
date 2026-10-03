using System.Text.Json;

namespace AqTracker.Core;

/// <summary>Small local cache of official app-server readings, independent of user settings and raw rollouts.</summary>
public sealed class QuotaSnapshotStore
{
    private readonly string _path;
    private readonly string _initialPath;
    private readonly bool _persistent;
    private readonly List<TimedQuotaUsage> _memory = [];
    private readonly List<TimedQuotaUsage> _initialMemory = [];
    public QuotaSnapshotStore(string? path = null, bool persistent = true) { _persistent = persistent; _path = path ?? Path.Combine(UserFolders.ApplicationData, "AqTracker", "quota-history.json"); _initialPath = Path.ChangeExtension(_path, "initial.json"); }

    public IReadOnlyList<TimedQuotaUsage> Read()
    {
        try
        {
            if (!_persistent) return _memory.ToArray();
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<TimedQuotaUsage>>(File.ReadAllText(_path))?.Where(x => x is not null).Where(IsValid).OrderBy(x => x.At).ToArray() ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public IReadOnlyList<TimedQuotaUsage> Append(DateTimeOffset at, double usedPercent)
    {
        if (!Net48Compatibility.IsFinite(usedPercent)) return Read();
        var previous = Read();
        var values = previous.Append(new TimedQuotaUsage(at, Net48Compatibility.Clamp(usedPercent, 0, 100)))
            .Where(x => x.At >= at.AddDays(-62)).GroupBy(x => x.At.LocalDateTime.Date)
            .Select(group => group.OrderBy(x => x.At).Last()).OrderBy(x => x.At).ToArray();
        if (previous.SequenceEqual(values)) return values;
        if (!_persistent) { _memory.Clear(); _memory.AddRange(values); return values; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(values));
            if (File.Exists(_path)) File.Replace(temporary, _path, null);
            else File.Move(temporary, _path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return values;
    }

    public IReadOnlyList<TimedQuotaUsage> ReadInitial() => Read(_initialPath, _initialMemory);

    /// <summary>Records only the first observed valid weekly snapshot on the actual local day.</summary>
    public IReadOnlyList<TimedQuotaUsage> CaptureInitial(DateTimeOffset at, double usedPercent, DateTimeOffset now)
    {
        var current = ReadInitial();
        if (!Net48Compatibility.IsFinite(usedPercent) || at > now || at.LocalDateTime.Date != now.LocalDateTime.Date || current.Any(x => x.At.LocalDateTime.Date == now.LocalDateTime.Date)) return current;
        var values = current.Append(new TimedQuotaUsage(at, Net48Compatibility.Clamp(usedPercent, 0, 100))).Where(x => x.At >= now.AddDays(-62)).OrderBy(x => x.At).ToArray();
        Write(_initialPath, _initialMemory, values); return values;
    }

    private IReadOnlyList<TimedQuotaUsage> Read(string path, List<TimedQuotaUsage> memory)
    {
        try { if (!_persistent) return memory.ToArray(); if (!File.Exists(path)) return []; return JsonSerializer.Deserialize<List<TimedQuotaUsage>>(File.ReadAllText(path))?.Where(x => x is not null).Where(IsValid).GroupBy(x => x.At.LocalDateTime.Date).Select(group => group.OrderBy(x => x.At).First()).OrderBy(x => x.At).ToArray() ?? []; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Write(string path, List<TimedQuotaUsage> memory, IReadOnlyList<TimedQuotaUsage> values)
    {
        if (!_persistent) { memory.Clear(); memory.AddRange(values); return; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(values)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsValid(TimedQuotaUsage value) => value.At != default && Net48Compatibility.IsFinite(value.UsedPercent);
}
