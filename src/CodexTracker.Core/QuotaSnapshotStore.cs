using System.Text.Json;

namespace CodexTracker.Core;

/// <summary>Small local cache of official app-server readings, independent of user settings and raw rollouts.</summary>
public sealed class QuotaSnapshotStore
{
    private readonly string _path;
    private readonly bool _persistent;
    private readonly List<TimedQuotaUsage> _memory = [];
    public QuotaSnapshotStore(string? path = null, bool persistent = true) { _persistent = persistent; _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexTracker", "quota-history.json"); }

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

    private static bool IsValid(TimedQuotaUsage value) => value.At != default && Net48Compatibility.IsFinite(value.UsedPercent);
}
