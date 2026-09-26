namespace ReviewForge.Core.Domain;

/// <summary>Exponential backoff for heads whose runs keep failing.</summary>
public sealed record FailureBackoffPolicy(TimeSpan Base, TimeSpan Max)
{
    public static readonly FailureBackoffPolicy Default = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8));
}

public static class FailureBackoff
{
    /// <summary>
    /// Counts consecutive failed runs at <paramref name="headSha"/> (newest first) and
    /// returns the time before which the head must not be re-enqueued, or null when
    /// the head may run now. A successful run resets the streak; a headed failure at a
    /// different head breaks it. Head-less failure records (empty HeadSha — endpoint
    /// submits that died in fetch before the head was known, P2-33) count toward the
    /// streak for any head and never break it. In-flight shells (CompletedAt == null)
    /// prove nothing about failure and are skipped entirely; the startup reaper
    /// converts stale ones into real records.
    /// </summary>
    public static DateTimeOffset? BlockedUntil(
        IReadOnlyList<ReviewRun> recentRuns,
        string headSha,
        DateTimeOffset now,
        FailureBackoffPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(policy.Base, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.Max, policy.Base);

        var streak = 0;
        DateTimeOffset? lastFailure = null;
        foreach (var run in recentRuns)
        {
            // In-flight shell (stages 75→100 window, or orphaned by a crash): not a
            // failure — and must not break a real failure streak either. Skipped.
            if (run.CompletedAt is null)
            {
                continue;
            }

            // A headed failure at a different head breaks the streak. Head-less
            // failures count for whatever head the submit carries — the fetch died
            // before we learned the head, so the record is head-agnostic.
            var differentHead = run.HeadSha is { Length: > 0 }
                && !string.Equals(run.HeadSha, headSha, StringComparison.Ordinal);
            if (run.Success || differentHead)
            {
                break;
            }

            streak++;
            lastFailure ??= run.CompletedAt;
        }

        if (streak == 0 || lastFailure is null)
        {
            return null;
        }

        var delay = TimeSpan.FromTicks(Math.Min(
            policy.Base.Ticks * (1L << Math.Min(streak - 1, 20)),
            policy.Max.Ticks));
        var blockedUntil = lastFailure.Value + delay;
        return blockedUntil > now ? blockedUntil : null;
    }
}
