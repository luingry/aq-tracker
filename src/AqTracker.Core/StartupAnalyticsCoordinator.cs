namespace AqTracker.Core;

/// <summary>
/// Joins the independent startup reads without forcing either source to wait for the other.
/// If local analytics wins the race, its completed result is retained until the first quota
/// snapshot arrives; if quota wins, analytics applies against that snapshot immediately.
/// </summary>
public sealed class StartupAnalyticsCoordinator
{
    private readonly object _gate = new();
    private RateLimitSnapshot? _snapshot;
    private UsageAnalytics? _pendingUsage;
    private long _connectionGeneration;

    public long BeginConnection()
    {
        lock (_gate)
        {
            _snapshot = null;
            return ++_connectionGeneration;
        }
    }

    public bool IsCurrent(long connectionGeneration)
    {
        lock (_gate) return connectionGeneration == _connectionGeneration;
    }

    public (RateLimitSnapshot Snapshot, UsageAnalytics Usage)? OnAnalyticsReady(UsageAnalytics usage, bool isDetailed)
    {
        lock (_gate)
        {
            if (!isDetailed) return null;
            if (_snapshot is { } snapshot) return (snapshot, usage);
            _pendingUsage = usage;
            return null;
        }
    }

    public UsageAnalytics? OnSnapshot(RateLimitSnapshot snapshot, bool isDetailed, long connectionGeneration)
    {
        lock (_gate)
        {
            if (connectionGeneration != _connectionGeneration) return null;
            _snapshot = snapshot;
            if (!isDetailed)
            {
                _pendingUsage = null;
                return null;
            }

            var usage = _pendingUsage;
            _pendingUsage = null;
            return usage;
        }
    }

}
