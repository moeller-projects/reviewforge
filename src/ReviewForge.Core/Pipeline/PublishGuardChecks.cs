namespace ReviewForge.Core.Pipeline;

/// <summary>Shared claim guard used by the stages that perform external writes.</summary>
public static class PublishGuardChecks
{
    /// <summary>
    /// Throws when the run's claim is gone (<see cref="ReviewContext.PublishGuard"/> returns
    /// false). No-op when no guard is configured (Core tests and embedded hosts).
    /// </summary>
    public static void ThrowIfClaimLost(ReviewContext ctx, string when)
    {
        if (ctx.PublishGuard is not null && !ctx.PublishGuard())
        {
            throw new InvalidOperationException($"review claim expired {when}");
        }
    }
}
