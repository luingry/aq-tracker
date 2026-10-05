namespace AqTracker.Core;

/// <summary>
/// Decides whether a Codex completion first seen after startup is new work. Opening the
/// Codex desktop appends <c>thread_settings_applied</c> to the last chat's rollout, which
/// pulls an old file back into the scan; its historical <c>task_complete</c> is unseen but
/// did not just happen and must not become unread.
/// </summary>
public static class CompletionNoveltyPolicy
{
    /// <summary>Tolerates the delay between a turn finishing and the first snapshot that reads it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    public static bool IsNew(CompletedAgentWork work, DateTimeOffset trackingSince) =>
        work.CompletedAt >= trackingSince - Grace;
}
