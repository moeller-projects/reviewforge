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
    /// the head may run now. A successful or different-head run resets the streak.
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
            if (run.Success || !string.Equals(run.HeadSha, headSha, StringComparison.Ordinal))
            {
                break;
            }

            streak++;
            lastFailure ??= run.CompletedAt ?? run.StartedAt;
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
