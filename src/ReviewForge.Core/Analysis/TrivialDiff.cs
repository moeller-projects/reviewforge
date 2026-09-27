using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Pure predicate for the trivial-diff fast path: an iteration whose post-exclusion diff
/// adds zero reviewable lines (deletions, context, and binary/mode-only churn do not
/// count) and that has no open human threads awaiting an answer gets a clean vote without
/// spending an LLM call. The conservative "added lines only" definition plus the
/// pending-reply guard keep the skip from swallowing real work.
/// </summary>
public static class TrivialDiff
{
    public static bool IsTrivial(
        DiffIndex diff,
        IReadOnlyCollection<PendingReply> pendingReplies,
        IReadOnlyCollection<string> reviewableFiles)
    {
        if (pendingReplies.Count > 0)
        {
            return false; // a human is waiting — the agent must run
        }

        long total = 0;
        foreach (var file in reviewableFiles)
        {
            total += diff.AddedLineCount(file);
            if (total > 0)
            {
                return false;
            }
        }

        return true;
    }
}
