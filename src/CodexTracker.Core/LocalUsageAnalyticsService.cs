using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CodexTracker.Core;

public sealed record TokenUsageBreakdown(long CachedReadTokens, long InputTokens, long OutputTokens, long ReasoningTokens, decimal CachedReadCostUsd = 0, decimal InputCostUsd = 0, decimal OutputCostUsd = 0, decimal ReasoningCostUsd = 0)
{
    public long TotalTokens => CachedReadTokens + InputTokens + OutputTokens + ReasoningTokens;
    public decimal TotalCostUsd => CachedReadCostUsd + InputCostUsd + OutputCostUsd + ReasoningCostUsd;
    public static TokenUsageBreakdown Zero { get; } = new(0, 0, 0, 0);
    public static TokenUsageBreakdown operator +(TokenUsageBreakdown left, TokenUsageBreakdown right) => new(
        left.CachedReadTokens + right.CachedReadTokens, left.InputTokens + right.InputTokens,
        left.OutputTokens + right.OutputTokens, left.ReasoningTokens + right.ReasoningTokens,
        left.CachedReadCostUsd + right.CachedReadCostUsd, left.InputCostUsd + right.InputCostUsd,
        left.OutputCostUsd + right.OutputCostUsd, left.ReasoningCostUsd + right.ReasoningCostUsd);
}

public sealed record ModelUsage(string Model, long Tokens, decimal CostUsd, bool Priced, TokenUsageBreakdown? Breakdown = null);
public sealed record DailyTokenUsage(DateTime Day, long Tokens, decimal UsdCost = 0, decimal BrlCost = 0, TokenUsageBreakdown? Breakdown = null);
public sealed record DailyQuotaUsage(DateTime Day, double? UsedPercent);
public sealed record TimedQuotaUsage(DateTimeOffset At, double UsedPercent);
public sealed record TimedTokenUsage(DateTimeOffset At, long Tokens, decimal CostUsd = 0, TokenUsageBreakdown? Breakdown = null);
public sealed record TimedModelUsage(DateTimeOffset At, string Model, long Tokens, decimal CostUsd = 0, bool Priced = false, TokenUsageBreakdown? Breakdown = null);
public sealed record ChatUsage(string ThreadId, string? ProjectPath, string? Title, long Tokens, decimal CostUsd, long PricedTokens, TokenUsageBreakdown Breakdown, DateTimeOffset LastUpdatedAt);
public sealed record UsageWindowEstimate(long Tokens, decimal CostUsd, decimal CostBrl);
public sealed record UsageAnalytics(long TodayTokens, long MonthTokens, decimal MonthUsd, decimal MonthBrl, double CoveragePercent, IReadOnlyList<ModelUsage> Models, decimal TodayUsd = 0, decimal TodayBrl = 0, IReadOnlyList<DailyTokenUsage>? DailySeries = null, IReadOnlyList<TimedTokenUsage>? Timeline = null, decimal UsdBrl = 0, IReadOnlyList<TimedModelUsage>? ModelTimeline = null, IReadOnlyList<ChatUsage>? Chats = null, IReadOnlyList<TimedQuotaUsage>? QuotaTimeline = null)
{
    public long TokensInWindow(DateTimeOffset startInclusive, DateTimeOffset endExclusive) =>
        (Timeline ?? []).Where(x => x.At >= startInclusive && x.At < endExclusive).Sum(x => x.Tokens);

    public UsageWindowEstimate EstimateInWindow(DateTimeOffset startInclusive, DateTimeOffset endExclusive)
    {
        var events = (Timeline ?? []).Where(x => x.At >= startInclusive && x.At < endExclusive).ToArray();
        var costUsd = events.Sum(x => x.CostUsd);
        return new(events.Sum(x => x.Tokens), costUsd, costUsd * UsdBrl);
    }

    public IReadOnlyList<ModelUsage> ModelsInWindow(DateTimeOffset startInclusive, DateTimeOffset endExclusive) =>
        (ModelTimeline ?? []).Where(x => x.At >= startInclusive && x.At < endExclusive)
            .GroupBy(x => new { x.Model, x.Priced })
            .Select(group => new ModelUsage(group.Key.Model, group.Sum(x => x.Tokens), group.Sum(x => x.CostUsd), group.Key.Priced,
                group.Aggregate(TokenUsageBreakdown.Zero, (breakdown, item) => breakdown + (item.Breakdown ?? TokenUsageBreakdown.Zero))))
            .OrderByDescending(x => x.Tokens)
            .ToArray();

    public long? TokensInWeeklyWindow(QuotaWindow? weekly) => weekly?.ResetsAt is { } reset
        ? TokensInWindow(reset.AddDays(-7), reset)
        : null;

    public UsageWindowEstimate? EstimateInWeeklyWindow(QuotaWindow? weekly) => weekly?.ResetsAt is { } reset
        ? EstimateInWindow(reset.AddDays(-7), reset)
        : null;
}

/// <summary>Chooses the closing weekly-quota reading for each local calendar day.</summary>
public static class DailyQuotaSeries
{
    public static IReadOnlyList<DailyQuotaUsage> CloseByLocalDay(IEnumerable<TimedQuotaUsage> readings, DateTime month, DateTimeOffset now, double? liveTodayPercent = null)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        var lastDay = DateTime.DaysInMonth(month.Year, month.Month);
        var closing = readings.Where(x => Net48Compatibility.IsFinite(x.UsedPercent) && x.At <= now && x.At.LocalDateTime.Year == month.Year && x.At.LocalDateTime.Month == month.Month)
            .GroupBy(x => x.At.LocalDateTime.Date)
            .ToDictionary(group => group.Key, group => group.OrderBy(x => x.At).Last().UsedPercent);
        var today = now.LocalDateTime.Date;
        return Enumerable.Range(1, lastDay).Select(day =>
        {
            var date = first.AddDays(day - 1);
            if (date > today) return new DailyQuotaUsage(date, null);
            if (date == today && liveTodayPercent.HasValue && Net48Compatibility.IsFinite(liveTodayPercent.Value)) return new DailyQuotaUsage(date, Net48Compatibility.Clamp(liveTodayPercent.Value, 0, 100));
            return new DailyQuotaUsage(date, closing.TryGetValue(date, out var value) ? Net48Compatibility.Clamp(value, 0, 100) : null);
        }).ToArray();
    }
}

public sealed class LocalUsageAnalyticsService
{
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _maxParseParallelism;
    private readonly string? _stateDatabasePath;
    private ThreadModelIndex? _threadModelIndex;
    private string? _threadModelIndexPath;
    private ThreadTitleIndex? _threadTitleIndex;
    private string? _threadTitleIndexPath;

    public LocalUsageAnalyticsService(Func<DateTimeOffset>? clock = null, int? maxParseParallelism = null, string? stateDatabasePath = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _maxParseParallelism = Math.Max(1, Math.Min(2, maxParseParallelism ?? Environment.ProcessorCount));
        _stateDatabasePath = stateDatabasePath;
    }
    public int FilesParsedLastRead { get; private set; }
    public int FilesRebuiltLastRead { get; private set; }
    public int FilesAppendedLastRead { get; private set; }
    public long BytesReadLastRead { get; private set; }
    public int LogicalStreamsLastRead { get; private set; }
    public int DuplicatePhysicalFilesIgnoredLastRead { get; private set; }
    public int CachedBucketCount => _cache.Values.Sum(x => x.Buckets.Count);
    public int CachedTimelineEntryCount => _cache.Values.Sum(x => x.Timeline.Count);
    private static readonly Dictionary<string, (decimal Input, decimal Cached, decimal Output)> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6-sol"] = (5m, .5m, 30m), ["gpt-5.6-terra"] = (2.5m, .25m, 15m),
        ["gpt-5.6-luna"] = (1m, .1m, 6m), ["gpt-5.3-codex"] = (1.75m, .175m, 14m)
    };

    public Task<UsageAnalytics> ReadAsync(decimal usdBrl, string? root = null) => Task.Run(() => Read(usdBrl, root));

    public static IReadOnlyList<string> DefaultRoots(string userProfile) =>
    [
        Path.Combine(userProfile, ".codex", "sessions"),
        Path.Combine(userProfile, ".codex", "archived_sessions")
    ];

    public UsageAnalytics Read(decimal usdBrl, string? root = null)
    {
        FilesParsedLastRead = 0;
        FilesRebuiltLastRead = 0;
        FilesAppendedLastRead = 0;
        BytesReadLastRead = 0;
        LogicalStreamsLastRead = 0;
        DuplicatePhysicalFilesIgnoredLastRead = 0;
        var stopwatch = Stopwatch.StartNew();
        var projectRoots = new ProjectRootResolver();
        var now = _clock();
        // An active seven-day quota window can start no earlier than seven days ago.
        // Keep one extra day for clock/refresh skew while bounding memory independently
        // of the total session-history size.
        var timelineCutoff = now.ToUniversalTime().AddDays(-8);
        var buckets = new Dictionary<BucketKey, Aggregate>();
        var timeline = new Dictionary<TimelineKey, TimelineAggregate>();
        var modelTimeline = new Dictionary<ModelTimelineKey, TimelineAggregate>();
        var quotaTimeline = new Dictionary<QuotaTimelineKey, TimedQuotaUsage>();
        var roots = root is not null
            ? [root]
            : DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var fallbackModels = ReadFallbackModels(root);
        var fallbackTitles = ReadFallbackTitles(root);
        var files = roots.Where(Directory.Exists).SelectMany(path => Directory.EnumerateFiles(path, "*.jsonl", SearchOption.AllDirectories))
            .Select(file => Describe(file)).ToArray();
        if (files.Length == 0) return new(0, 0, 0, 0, 0, []);
        var activePaths = files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _cache.Keys.Where(x => !activePaths.Contains(x)).ToArray()) _cache.Remove(stale);
        var candidateCaches = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        var parsePlans = new List<ParsePlan>(files.Length);
        foreach (var file in files)
        {
            try
            {
                var signature = FileSignature.Create(file.Path);
                var fallbackModel = fallbackModels.TryGetValue(file.FileId, out var indexedModel) ? indexedModel : "unknown";
                // Active JSONL files may end mid-record. Do not advance the cache offset over an incomplete line;
                // the next read rebuilds this file after the writer commits its terminating newline.
                var hasCommittedTail = HasFinalNewline(file.Path, signature.Length);
                if (!hasCommittedTail)
                {
                    parsePlans.Add(new(file, signature, null, ParseKind.Partial, fallbackModel));
                    _cache.Remove(file.Path);
                    continue;
                }
                if (!_cache.TryGetValue(file.Path, out var cached) || cached.IsFork != file.IsFork || cached.FallbackModel != fallbackModel || signature.Length < cached.Signature.Length || (signature.Length == cached.Signature.Length && signature.LastWriteUtcTicks != cached.Signature.LastWriteUtcTicks) || (signature.Length > cached.Signature.Length && PrefixMarker.Create(file.Path, cached.Signature.Length) != cached.PrefixMarker))
                {
                    parsePlans.Add(new(file, signature, null, ParseKind.Rebuild, fallbackModel));
                }
                else if (signature.Length > cached.Signature.Length)
                {
                    parsePlans.Add(new(file, signature, cached, ParseKind.Append, fallbackModel));
                }
                else candidateCaches[file.Path] = cached;
            }
            catch (Exception ex) { SanitizedLogger.Write("Analytics file error: " + ex.GetType().Name); }
        }
        var parseResults = new ParsePlanResult[parsePlans.Count];
        Parallel.For(0, parsePlans.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxParseParallelism }, index =>
        {
            var plan = parsePlans[index];
            try
            {
                var offset = plan.Kind == ParseKind.Append ? plan.Previous!.Signature.Length : 0;
                var baseline = plan.Kind == ParseKind.Append ? plan.Previous!.LastTotals : null;
                var model = plan.Kind == ParseKind.Append ? plan.Previous!.LastModel : plan.FallbackModel;
                parseResults[index] = new(plan, ParseAggregate(plan.File.Path, offset, plan.Signature.Length, baseline, model, plan.File.IsFork, timelineCutoff), null);
            }
            catch (Exception ex) { parseResults[index] = new(plan, null, ex); }
        });
        foreach (var result in parseResults)
        {
            if (result.Error is not null) { SanitizedLogger.Write("Analytics file error: " + result.Error.GetType().Name); continue; }
            var plan = result.Plan;
            var parsed = result.Parsed!;
            if (parsed.MalformedLineCount > 0)
                SanitizedLogger.Write("Analytics malformed JSONL lines skipped: " + parsed.MalformedLineCount);
            CachedFile cached;
            if (plan.Kind == ParseKind.Append)
            {
                cached = plan.Previous!;
                var previousLength = cached.Signature.Length;
                MergeBuckets(cached.Buckets, parsed.Buckets);
                MergeTimeline(cached.Timeline, parsed.Timeline);
                MergeModelTimeline(cached.ModelTimeline, parsed.ModelTimeline);
                MergeQuotaTimeline(cached.QuotaTimeline, parsed.QuotaTimeline);
                cached = cached with { Signature = plan.Signature, FallbackModel = plan.FallbackModel, LastTotals = parsed.LastTotals ?? cached.LastTotals, LastModel = parsed.LastModel, LastUsageAt = MostRecent(cached.LastUsageAt, parsed.LastUsageAt), PrefixMarker = PrefixMarker.Create(plan.File.Path, plan.Signature.Length) };
                FilesAppendedLastRead++;
                BytesReadLastRead += plan.Signature.Length - previousLength;
            }
            else
            {
                cached = new CachedFile(plan.Signature, plan.File.IsFork, plan.FallbackModel, parsed.Buckets, parsed.Timeline, parsed.ModelTimeline, parsed.QuotaTimeline, parsed.LastTotals, parsed.LastModel, parsed.LastUsageAt, plan.Kind == ParseKind.Partial ? "" : PrefixMarker.Create(plan.File.Path, plan.Signature.Length));
                FilesRebuiltLastRead++;
                BytesReadLastRead += plan.Signature.Length;
            }
            FilesParsedLastRead++;
            if (plan.Kind != ParseKind.Partial) _cache[plan.File.Path] = cached;
            candidateCaches[plan.File.Path] = cached;
        }
        foreach (var cached in candidateCaches.Values)
        {
            foreach (var staleTimeline in cached.Timeline.Keys.Where(key => key.At < timelineCutoff).ToArray()) cached.Timeline.Remove(staleTimeline);
            foreach (var staleTimeline in cached.ModelTimeline.Keys.Where(key => key.At < timelineCutoff).ToArray()) cached.ModelTimeline.Remove(staleTimeline);
        }
        var candidates = files.Where(file => candidateCaches.ContainsKey(file.Path)).Select(file => new LogicalFileCandidate(file, candidateCaches[file.Path])).ToList();
        // Only collapse physical copies when their bytes prove that one is a checkpoint-prefix
        // of the other. Equal session ids alone are insufficient: a subagent JSONL embeds its
        // parent metadata and independent segments may legitimately share ancestry.
        var logicalCandidates = candidates.GroupBy(x => x.File.FileId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(SelectNonOverlappingCandidates)
            .ToArray();
        foreach (var candidate in logicalCandidates)
        {
            MergeBuckets(buckets, candidate.Cache.Buckets);
            MergeTimeline(timeline, candidate.Cache.Timeline);
            MergeModelTimeline(modelTimeline, candidate.Cache.ModelTimeline);
            MergeQuotaTimeline(quotaTimeline, candidate.Cache.QuotaTimeline);
        }
        LogicalStreamsLastRead = logicalCandidates.Length;
        DuplicatePhysicalFilesIgnoredLastRead = candidates.Count - logicalCandidates.Length;
        var month = buckets.Where(x => x.Key.Day.Year == now.Year && x.Key.Day.Month == now.Month);
        var candidatesByThread = logicalCandidates.GroupBy(x => x.File.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
        string RootThread(string threadId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (candidatesByThread.TryGetValue(threadId, out var current) && !string.IsNullOrWhiteSpace(current.File.ParentThreadId) && seen.Add(threadId))
                threadId = current.File.ParentThreadId!;
            return threadId;
        }
        var chatAggregates = new Dictionary<string, ChatAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in logicalCandidates)
        {
            var rootThreadId = RootThread(candidate.File.ThreadId);
            var monthAggregate = candidate.Cache.Buckets.Where(x => x.Key.Day.Year == now.Year && x.Key.Day.Month == now.Month).ToArray();
            if (monthAggregate.Length == 0) continue;
            var aggregate = chatAggregates.TryGetValue(rootThreadId, out var currentAggregate) ? currentAggregate : ChatAggregate.Empty;
            var rootProjectPath = candidatesByThread.TryGetValue(rootThreadId, out var rootCandidate) ? projectRoots.Resolve(rootCandidate.File.ProjectPath) : null;
            aggregate = aggregate with
            {
                Usage = aggregate.Usage + CalculateBreakdown(monthAggregate),
                Tokens = aggregate.Tokens + monthAggregate.Sum(x => x.Value.Total),
                PricedTokens = aggregate.PricedTokens + monthAggregate.Where(x => Prices.Keys.Any(price => string.Equals(price, x.Key.Model, StringComparison.OrdinalIgnoreCase))).Sum(x => x.Value.Total),
                ProjectPath = aggregate.ProjectPath ?? rootProjectPath ?? projectRoots.Resolve(candidate.File.ProjectPath),
                LastUpdatedAt = MostRecent(aggregate.LastUpdatedAt, candidate.Cache.LastUsageAt)
            };
            chatAggregates[rootThreadId] = aggregate;
        }
        var chats = chatAggregates.Select(pair => new ChatUsage(pair.Key, pair.Value.ProjectPath,
            fallbackTitles.TryGetValue(pair.Key, out var title) ? title : null, pair.Value.Tokens, pair.Value.Usage.TotalCostUsd, pair.Value.PricedTokens, pair.Value.Usage, pair.Value.LastUpdatedAt ?? DateTimeOffset.MinValue))
            .OrderByDescending(chat => chat.LastUpdatedAt)
            .ThenByDescending(chat => chat.Tokens)
            .ThenBy(chat => chat.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(chat => chat.ThreadId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var models = month.GroupBy(x => x.Key.Model).Select(g =>
        {
            var tokens = g.Sum(x => x.Value.Total);
            var key = Prices.Keys.FirstOrDefault(k => string.Equals(g.Key, k, StringComparison.OrdinalIgnoreCase));
            var breakdown = CalculateBreakdown(g.Select(x => x.Value), key is null ? null : Prices[key]);
            if (key is null) return new ModelUsage(g.Key, tokens, 0, false, breakdown);
            return new ModelUsage(g.Key, tokens, breakdown.TotalCostUsd, true, breakdown);
        }).OrderByDescending(x => x.Tokens).ToArray();
        var total = models.Sum(x => x.Tokens);
        var todayBuckets = buckets.Where(x => x.Key.Day == now.LocalDateTime.Date).ToArray();
        var todayUsd = CalculateBreakdown(todayBuckets).TotalCostUsd;
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        var dailySeries = Enumerable.Range(1, daysInMonth)
            .Select(day =>
            {
                var date = new DateTime(now.Year, now.Month, day);
                var dayBuckets = buckets.Where(x => x.Key.Day == date).ToArray();
                var breakdown = CalculateBreakdown(dayBuckets);
                return new DailyTokenUsage(date, dayBuckets.Sum(x => x.Value.Total), breakdown.TotalCostUsd, breakdown.TotalCostUsd * usdBrl, breakdown);
            })
            .ToArray();
        var timedSeries = timeline.OrderBy(x => x.Key.At).Select(x => new TimedTokenUsage(x.Key.At, x.Value.Tokens, x.Value.CostUsd, x.Value.Breakdown)).ToArray();
        var timedModelSeries = modelTimeline.OrderBy(x => x.Key.At).Select(x => new TimedModelUsage(x.Key.At, x.Key.Model, x.Value.Tokens, x.Value.CostUsd, x.Key.Priced, x.Value.Breakdown)).ToArray();
        SanitizedLogger.Write("Analytics refreshed: models=" + models.Length + ", files=" + files.Length + ", streams=" + LogicalStreamsLastRead + ", duplicateSnapshots=" + DuplicatePhysicalFilesIgnoredLastRead + ", ms=" + stopwatch.ElapsedMilliseconds);
        var quotaSeries = quotaTimeline.Values.OrderBy(x => x.At).ToArray();
        return new(todayBuckets.Sum(x => x.Value.Total), total, models.Sum(x => x.CostUsd), models.Sum(x => x.CostUsd) * usdBrl, total == 0 ? 0 : 100d * models.Where(x => x.Priced).Sum(x => x.Tokens) / total, models, todayUsd, todayUsd * usdBrl, dailySeries, timedSeries, usdBrl, timedModelSeries, chats, quotaSeries);
    }

    private static TokenUsageBreakdown CalculateBreakdown(IEnumerable<Aggregate> aggregates, (decimal Input, decimal Cached, decimal Output)? price = null)
    {
        var total = TokenUsageBreakdown.Zero;
        foreach (var value in aggregates)
        {
            var cached = Math.Max(value.Cached, 0);
            var input = Math.Max(value.Input - cached, 0);
            var reasoning = Math.Max(value.Reasoning, 0);
            var output = Math.Max(value.Output - reasoning, 0);
            var million = 1_000_000m;
            var tariff = price.GetValueOrDefault();
            total += new TokenUsageBreakdown(cached, input, output, reasoning,
                price.HasValue ? cached * tariff.Cached / million : 0,
                price.HasValue ? input * tariff.Input / million : 0,
                price.HasValue ? output * tariff.Output / million : 0,
                price.HasValue ? reasoning * tariff.Output / million : 0);
        }
        return total;
    }

    private IReadOnlyDictionary<string, string> ReadFallbackModels(string? root)
    {
        var databasePath = _stateDatabasePath ?? (root is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite") : null);
        if (string.IsNullOrWhiteSpace(databasePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_threadModelIndex is null || !string.Equals(_threadModelIndexPath, databasePath, StringComparison.OrdinalIgnoreCase))
        {
            _threadModelIndex = new ThreadModelIndex(databasePath!);
            _threadModelIndexPath = databasePath;
        }
        return _threadModelIndex.Read();
    }

    private IReadOnlyDictionary<string, string> ReadFallbackTitles(string? root)
    {
        var databasePath = _stateDatabasePath ?? (root is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite") : null);
        if (string.IsNullOrWhiteSpace(databasePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_threadTitleIndex is null || !string.Equals(_threadTitleIndexPath, databasePath, StringComparison.OrdinalIgnoreCase))
        {
            _threadTitleIndex = new ThreadTitleIndex(databasePath!);
            _threadTitleIndexPath = databasePath;
        }
        return _threadTitleIndex.Read();
    }

    private static decimal CalculateCostUsd(IEnumerable<KeyValuePair<BucketKey, Aggregate>> buckets)
    {
        var total = 0m;
        foreach (var bucket in buckets)
        {
            var key = Prices.Keys.FirstOrDefault(priceKey => string.Equals(bucket.Key.Model, priceKey, StringComparison.OrdinalIgnoreCase));
            if (key is not null) total += CalculateBreakdown([bucket.Value], Prices[key]).TotalCostUsd;
        }
        return total;
    }

    private static TokenUsageBreakdown CalculateBreakdown(IEnumerable<KeyValuePair<BucketKey, Aggregate>> buckets)
    {
        var total = TokenUsageBreakdown.Zero;
        foreach (var bucket in buckets)
        {
            var key = Prices.Keys.FirstOrDefault(priceKey => string.Equals(bucket.Key.Model, priceKey, StringComparison.OrdinalIgnoreCase));
            total += CalculateBreakdown([bucket.Value], key is null ? null : Prices[key]);
        }
        return total;
    }

    private static FileDescriptor Describe(string path)
    {
        string? sessionId = null;
        string? fileId = null;
        DateTimeOffset? startedAt = null;
        var fork = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            for (var lineNumber = 0; lineNumber < 8; lineNumber++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                JsonElement meta;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "session_meta" && root.TryGetProperty("payload", out var realPayload) && realPayload.ValueKind == JsonValueKind.Object)
                    meta = realPayload;
                else if (root.TryGetProperty("session_meta", out var legacyMeta) && legacyMeta.ValueKind == JsonValueKind.Object)
                    meta = legacyMeta;
                else continue;
                // The first metadata record belongs to this physical rollout. Later records can
                // be inherited parent context embedded in a fork and must not replace its id.
                sessionId = ReadString(meta, "session_id");
                fileId = ReadString(meta, "id");
                var parentThreadId = ReadString(meta, "parent_thread_id") ?? ReadString(meta, "forked_from_id");
                var projectPath = ReadString(meta, "cwd");
                startedAt = ReadTimestamp(root) ?? ReadTimestamp(meta);
                fork = !string.IsNullOrWhiteSpace(ReadString(meta, "forked_from_id")) || ReadString(meta, "thread_source") == "subagent";
                return new(path, sessionId ?? path, fileId ?? path, parentThreadId, projectPath, fork, startedAt);
            }
        }
        catch { SanitizedLogger.Write("Analytics metadata skipped"); }
        return new(path, sessionId ?? path, fileId ?? path, null, null, fork, startedAt);
    }

    private static ParseAggregateResult ParseAggregate(string file, long offset, long snapshotLength, Totals? baseline, string model, bool isFork, DateTimeOffset timelineCutoff)
    {
        var buckets = new Dictionary<BucketKey, Aggregate>();
        var timeline = new Dictionary<TimelineKey, TimelineAggregate>();
        var modelTimeline = new Dictionary<ModelTimelineKey, TimelineAggregate>();
        var quotaTimeline = new Dictionary<QuotaTimelineKey, TimedQuotaUsage>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        using var bounded = new BoundedReadStream(stream, Math.Max(0, snapshotLength - offset));
        using var reader = new StreamReader(bounded);
        string? line;
        var malformedLineCount = 0;
        DateTimeOffset? lastUsageAt = null;
        while ((line = reader.ReadLine()) is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                var top = ReadString(root, "type"); var legacy = ReadString(payload, "type");
                var at = ReadTimestamp(root) ?? File.GetLastWriteTimeUtc(file);
                if (TryReadWeeklyQuota(payload, out var usedPercent))
                {
                    var quotaKey = new QuotaTimelineKey(at.LocalDateTime.Date);
                    if (!quotaTimeline.TryGetValue(quotaKey, out var prior) || at > prior.At) quotaTimeline[quotaKey] = new TimedQuotaUsage(at, usedPercent);
                }
                if ((top == "turn_context" || legacy == "turn_context") && ReadString(payload, "model") is { } currentModel) model = currentModel;
                else if (top == "event_msg" && legacy == "thread_settings_applied" &&
                         payload.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object &&
                         ReadString(settings, "model") is { } settingsModel) model = settingsModel;
                var type = top == "event_msg" ? ReadString(payload, "type") : legacy;
                if (type != "token_count" || !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("total_token_usage", out var total)) continue;
                var current = Totals.From(total); if (current is null) continue;
                // total_token_usage is cumulative per rollout. Its first snapshot is work processed
                // by that rollout even when context was inherited by a fork. A component decrease
                // starts a new accumulator segment, whose current snapshot must also be counted.
                var delta = baseline is not { } before
                    ? current.Value
                    : current.Value.IsMonotonicAfter(before) ? current.Value - before : current.Value;
                baseline = current;
                lastUsageAt = MostRecent(lastUsageAt, at);
                if (delta.Total > 0)
                {
                    var key = new BucketKey(at.LocalDateTime.Date, model);
                    buckets[key] = Net48Compatibility.GetValueOrDefault(buckets, key) + new Aggregate(delta.Input, delta.Cached, delta.Output, delta.Reasoning, delta.Total);
                    if (at >= timelineCutoff)
                    {
                        var timelineKey = new TimelineKey(at);
                        var priceKey = Prices.Keys.FirstOrDefault(key => string.Equals(model, key, StringComparison.OrdinalIgnoreCase));
                        var breakdown = CalculateBreakdown([new Aggregate(delta.Input, delta.Cached, delta.Output, delta.Reasoning, delta.Total)], priceKey is null ? null : Prices[priceKey]);
                        var costUsd = breakdown.TotalCostUsd;
                        timeline[timelineKey] = Net48Compatibility.GetValueOrDefault(timeline, timelineKey) + new TimelineAggregate(delta.Total, costUsd, breakdown);
                        var modelTimelineKey = new ModelTimelineKey(at, model, priceKey is not null);
                        modelTimeline[modelTimelineKey] = Net48Compatibility.GetValueOrDefault(modelTimeline, modelTimelineKey) + new TimelineAggregate(delta.Total, costUsd, breakdown);
                    }
                }
            }
            catch (JsonException) { malformedLineCount++; }
        }
        return new ParseAggregateResult(buckets, timeline, modelTimeline, quotaTimeline, baseline, model, lastUsageAt, malformedLineCount);
    }

    private static bool TryReadWeeklyQuota(JsonElement payload, out double usedPercent)
    {
        usedPercent = 0;
        if (!payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object ||
            !string.Equals(ReadString(limits, "limit_id"), "codex", StringComparison.OrdinalIgnoreCase) ||
            !TryReadOfficialWeeklyWindow(limits, "primary", out usedPercent) &&
            !TryReadOfficialWeeklyWindow(limits, "secondary", out usedPercent)) return false;
        return true;
    }

    private static bool TryReadOfficialWeeklyWindow(JsonElement limits, string role, out double usedPercent)
    {
        usedPercent = 0;
        return limits.TryGetProperty(role, out var window) && window.ValueKind == JsonValueKind.Object &&
               window.TryGetProperty("used_percent", out var used) && used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out usedPercent) &&
               window.TryGetProperty("window_minutes", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes) && minutes >= OfficialCodexQuotaWindows.WeeklyMinimumMinutes &&
               Net48Compatibility.IsFinite(usedPercent) && (usedPercent = Net48Compatibility.Clamp(usedPercent, 0, 100)) >= 0;
    }

    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static DateTimeOffset? ReadTimestamp(JsonElement element) => ReadString(element, "timestamp") is { } value && DateTimeOffset.TryParse(value, out var timestamp) ? timestamp.ToUniversalTime() : null;
    private sealed record FileDescriptor(string Path, string SessionId, string FileId, string? ParentThreadId, string? ProjectPath, bool IsFork, DateTimeOffset? StartedAt)
    {
        public string ThreadId => FileId;
    }
    private sealed record LogicalFileCandidate(FileDescriptor File, CachedFile Cache);
    private enum ParseKind { Partial, Rebuild, Append }
    private sealed record ParsePlan(FileDescriptor File, FileSignature Signature, CachedFile? Previous, ParseKind Kind, string FallbackModel);
    private sealed record ParsePlanResult(ParsePlan Plan, ParseAggregateResult? Parsed, Exception? Error);
    private sealed record CachedFile(FileSignature Signature, bool IsFork, string FallbackModel, Dictionary<BucketKey, Aggregate> Buckets, Dictionary<TimelineKey, TimelineAggregate> Timeline, Dictionary<ModelTimelineKey, TimelineAggregate> ModelTimeline, Dictionary<QuotaTimelineKey, TimedQuotaUsage> QuotaTimeline, Totals? LastTotals, string LastModel, DateTimeOffset? LastUsageAt, string PrefixMarker);
    private sealed record ParseAggregateResult(Dictionary<BucketKey, Aggregate> Buckets, Dictionary<TimelineKey, TimelineAggregate> Timeline, Dictionary<ModelTimelineKey, TimelineAggregate> ModelTimeline, Dictionary<QuotaTimelineKey, TimedQuotaUsage> QuotaTimeline, Totals? LastTotals, string LastModel, DateTimeOffset? LastUsageAt, int MalformedLineCount);
    private readonly record struct FileSignature(long Length, long LastWriteUtcTicks)
    {
        public static FileSignature Create(string path)
        {
            var info = new FileInfo(path);
            return new FileSignature(info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }
    private static class PrefixMarker
    {
        public static string Create(string path, long length)
        {
            const int chunk = 2048;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Feed(stream, hash, 0, (int)Math.Min(chunk, length));
            if (length > chunk) Feed(stream, hash, Math.Max(0, length - chunk), (int)Math.Min(chunk, length));
            return Net48Compatibility.ToHexString(hash.GetHashAndReset());
        }
        private static void Feed(FileStream stream, IncrementalHash hash, long start, int count)
        {
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[count]; var read = stream.Read(buffer, 0, count);
            if (read > 0) hash.AppendData(buffer, 0, read);
        }
    }
    private static bool HasFinalNewline(string path, long length)
    {
        if (length == 0) return true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(length - 1, SeekOrigin.Begin);
        var last = stream.ReadByte();
        return last is '\n' or '\r';
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;
        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly record struct BucketKey(DateTime Day, string Model);
    private readonly record struct TimelineKey(DateTimeOffset At);
    private readonly record struct ModelTimelineKey(DateTimeOffset At, string Model, bool Priced);
    private readonly record struct QuotaTimelineKey(DateTime Day);
    private readonly record struct TimelineAggregate(long Tokens, decimal CostUsd, TokenUsageBreakdown? Breakdown)
    {
        public static TimelineAggregate operator +(TimelineAggregate left, TimelineAggregate right) => new(left.Tokens + right.Tokens, left.CostUsd + right.CostUsd, (left.Breakdown ?? TokenUsageBreakdown.Zero) + (right.Breakdown ?? TokenUsageBreakdown.Zero));
    }
    private readonly record struct Aggregate(long Input, long Cached, long Output, long Reasoning, long Total)
    {
        public static Aggregate operator +(Aggregate left, Aggregate right) => new(left.Input + right.Input, left.Cached + right.Cached, left.Output + right.Output, left.Reasoning + right.Reasoning, left.Total + right.Total);
    }
    private readonly record struct ChatAggregate(string? ProjectPath, long Tokens, long PricedTokens, TokenUsageBreakdown Usage, DateTimeOffset? LastUpdatedAt)
    {
        public static ChatAggregate Empty => new(null, 0, 0, TokenUsageBreakdown.Zero, null);
    }

    private static DateTimeOffset? MostRecent(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (candidate is null || current is { } value && value >= candidate.Value) return current;
        return candidate;
    }

    private static void MergeBuckets(Dictionary<BucketKey, Aggregate> destination, IReadOnlyDictionary<BucketKey, Aggregate> source)
    {
        foreach (var pair in source) destination[pair.Key] = Net48Compatibility.GetValueOrDefault(destination, pair.Key) + pair.Value;
    }

    private static void MergeTimeline(Dictionary<TimelineKey, TimelineAggregate> destination, IReadOnlyDictionary<TimelineKey, TimelineAggregate> source)
    {
        foreach (var pair in source) destination[pair.Key] = Net48Compatibility.GetValueOrDefault(destination, pair.Key) + pair.Value;
    }

    private static void MergeModelTimeline(Dictionary<ModelTimelineKey, TimelineAggregate> destination, IReadOnlyDictionary<ModelTimelineKey, TimelineAggregate> source)
    {
        foreach (var pair in source) destination[pair.Key] = Net48Compatibility.GetValueOrDefault(destination, pair.Key) + pair.Value;
    }
    private static void MergeQuotaTimeline(Dictionary<QuotaTimelineKey, TimedQuotaUsage> destination, IReadOnlyDictionary<QuotaTimelineKey, TimedQuotaUsage> source)
    {
        foreach (var pair in source) if (!destination.TryGetValue(pair.Key, out var current) || pair.Value.At > current.At) destination[pair.Key] = pair.Value;
    }

    private static IEnumerable<LogicalFileCandidate> SelectNonOverlappingCandidates(IGrouping<string, LogicalFileCandidate> group)
    {
        var files = group.OrderBy(x => x.File.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < files.Length; index++)
        {
            var candidate = files[index];
            var coveredByAnother = files.Where((other, otherIndex) => otherIndex != index).Any(other =>
                other.Cache.Signature.Length > candidate.Cache.Signature.Length && IsPhysicalPrefix(candidate.File.Path, other.File.Path)
                || other.Cache.Signature.Length == candidate.Cache.Signature.Length && string.Compare(other.File.Path, candidate.File.Path, StringComparison.OrdinalIgnoreCase) < 0 && IsPhysicalPrefix(candidate.File.Path, other.File.Path));
            if (!coveredByAnother) yield return candidate;
        }
    }

    private static bool IsPhysicalPrefix(string prefixPath, string fullPath)
    {
        using var prefix = new FileStream(prefixPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var full = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (prefix.Length > full.Length) return false;
        var prefixBuffer = new byte[64 * 1024];
        var fullBuffer = new byte[64 * 1024];
        int read;
        while ((read = prefix.Read(prefixBuffer, 0, prefixBuffer.Length)) > 0)
        {
            if (full.Read(fullBuffer, 0, read) != read || !prefixBuffer.AsSpan(0, read).SequenceEqual(fullBuffer.AsSpan(0, read))) return false;
        }
        return true;
    }
    private readonly record struct Totals(long Input, long Cached, long Output, long Reasoning, long Total)
    {
        public static Totals Zero => new(0, 0, 0, 0, 0);
        public static Totals? From(JsonElement e)
        {
            long Get(string n) => e.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : 0;
            if (e.ValueKind != JsonValueKind.Object) return null;
            var input = Get("input_tokens");
            var output = Get("output_tokens");
            var reasoning = Get("reasoning_output_tokens");
            return new(input, Get("cached_input_tokens"), output, reasoning, input + output);
        }
        public bool IsMonotonicAfter(Totals other) => Input >= other.Input && Cached >= other.Cached && Output >= other.Output && Reasoning >= other.Reasoning;
        public static Totals operator -(Totals a, Totals b) => new(a.Input - b.Input, a.Cached - b.Cached, a.Output - b.Output, a.Reasoning - b.Reasoning, a.Total - b.Total);
    }
}
