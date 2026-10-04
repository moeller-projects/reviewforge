namespace ReviewForge.Core.Domain;

public enum ResolveGateDecision { Continue, Draft, AuthorNotAllowed, NoComments, NoNewComments }

public static class ResolveGate
{
    /// <summary>Gate decision for a resolve run. Eligible comments mirror the collect stage:
    /// only ACTIVE threads (never Fixed/Closed) whose LAST comment is human — plus bot-last
    /// threads re-queued by a deferred action. Anything else would run the whole pipeline
    /// with zero resolvable comments and persist a no-work run instead of terminating here.</summary>
    public static ResolveGateDecision Evaluate(
        PullRequest pr,
        IReadOnlyList<ReviewThread> threads,
        DateTimeOffset? resolveWatermark,
        bool authorAllowed,
        IReadOnlySet<int>? deferredThreadIds = null)
    {
        if (pr.IsDraft) return ResolveGateDecision.Draft;
        if (!authorAllowed) return ResolveGateDecision.AuthorNotAllowed;
        if (!threads.SelectMany(t => t.Comments).Any(c => !c.IsBot)) return ResolveGateDecision.NoComments;
        var eligible = threads
            .Where(t => t.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed))
            .Where(t =>
            {
                if (t.Comments.Count == 0 || !t.Comments.Any(c => !c.IsBot)) return false;
                if (!t.Comments[^1].IsBot) return true;
                // Bot-last threads are skipped by the collector — unless a deferred action
                // re-queues them (the deferred bypass is the one legal bot-last retry).
                return deferredThreadIds is not null && deferredThreadIds.Contains(t.Id);
            })
            .SelectMany(t => t.Comments.Where(c => !c.IsBot))
            .ToArray();
        return resolveWatermark is { } watermark && eligible.All(c => c.PublishedAt <= watermark)
            ? ResolveGateDecision.NoNewComments
            : ResolveGateDecision.Continue;
    }
}
