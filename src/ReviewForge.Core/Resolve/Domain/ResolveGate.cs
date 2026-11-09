using ReviewForge.Core.Domain;
namespace ReviewForge.Core.Resolve.Domain;

public enum ResolveGateDecision { Continue, Draft, AuthorNotAllowed, NoComments, NoNewComments }

public static class ResolveGate
{
    /// <summary>Gate decision for a resolve run. Eligible comments mirror the collect stage:
    /// active threads with new human feedback qualify. A bot-last thread remains eligible
    /// only when its latest human comment is a fresh creator `/resolve` command or a deferred
    /// retry; other bot-last threads terminate before the no-work pipeline.</summary>
    public static ResolveGateDecision Evaluate(
        PullRequest pr,
        IReadOnlyList<ReviewThread> threads,
        DateTimeOffset? resolveWatermark,
        bool authorAllowed,
        IReadOnlySet<int>? deferredThreadIds = null,
        bool requireCommand = false,
        string? creatorId = null)
    {
        if (pr.IsDraft) return ResolveGateDecision.Draft;
        if (!authorAllowed) return ResolveGateDecision.AuthorNotAllowed;
        if (!threads.SelectMany(t => t.Comments).Any(c => c.IsHuman)) return ResolveGateDecision.NoComments;
        var commandThreadIds = requireCommand
            ? ResolveCommandDetector.Scan(threads, creatorId ?? string.Empty, resolveWatermark)
                .Select(command => command.ThreadId)
                .ToHashSet()
            : null;
        if (requireCommand)
        {
            var hasDeferredThread = deferredThreadIds is not null && threads.Any(thread =>
                deferredThreadIds.Contains(thread.Id)
                && thread.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                && thread.Comments.Any(comment => comment.IsHuman));
            if (commandThreadIds!.Count == 0 && !hasDeferredThread)
                return ResolveGateDecision.NoNewComments;
        }
        // A previously deferred thread must be retried even when its human comments
        // predate the completed-run watermark.
        if (deferredThreadIds is not null && threads.Any(t =>
                deferredThreadIds.Contains(t.Id)
                && t.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                && t.Comments.Any(c => c.IsHuman)))
        {
            return ResolveGateDecision.Continue;
        }
        var eligible = threads
            .Where(t => t.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed))
            .Where(t =>
            {
                if (t.Comments.Count == 0 || !t.Comments.Any(c => c.IsHuman)) return false;
                if (t.LastNonSystemComment is {IsHuman: true}) return true;
                if (commandThreadIds?.Contains(t.Id) == true
                    || (commandThreadIds is null
                        && ResolveCommandDetector.Find(t, creatorId ?? string.Empty, resolveWatermark) is not null))
                    return true;
                // Non-human-last threads are skipped by the collector unless a creator command
                // was acknowledged or a deferred action re-queued the thread.
                return deferredThreadIds is not null && deferredThreadIds.Contains(t.Id);
            })
            .SelectMany(t => t.Comments.Where(c => c.IsHuman))
            .ToArray();
        return resolveWatermark is { } watermark && eligible.All(c => c.PublishedAt <= watermark)
            ? ResolveGateDecision.NoNewComments
            : ResolveGateDecision.Continue;
    }
}
