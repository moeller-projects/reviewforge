namespace ReviewForge.Core.Domain;

public enum TriageOp
{
    None,
    Resolve,
    Answer,
    Reopen
}

public sealed record TriageOperation(int ThreadId, TriageOp Op, string? Comment, ReviewThreadStatus? NewStatus);

/// <summary>
/// Pure triage rules for existing bot threads. Automation contract:
/// - active thread whose finding no longer reproduces → resolve (Fixed) with a note
/// - pending human reply + agent action → apply it (answer / resolve / reopen with follow-up)
/// - pending human reply without agent action → flag for manual answer (warning, no write)
/// - active thread whose finding still reproduces → leave untouched
/// </summary>
public static class ThreadTriage
{
    public static IReadOnlyList<TriageOperation> Plan(
        IReadOnlyList<ReviewThread> botThreads,
        IReadOnlyCollection<string> currentFindingKeys,
        IReadOnlyList<ThreadAction> agentActions)
        => Plan(botThreads, currentFindingKeys, agentActions, postedText => postedText);

    /// <param name="replyTextForMatch">Maps a planned comment to the exact text a previous
    /// attempt would have posted (e.g. CommentFormatter.WithBotPreamble).</param>
    public static IReadOnlyList<TriageOperation> Plan(
        IReadOnlyList<ReviewThread> botThreads,
        IReadOnlyCollection<string> currentFindingKeys,
        IReadOnlyList<ThreadAction> agentActions,
        Func<string, string> replyTextForMatch)
    {
        var operations = new List<TriageOperation>();

        foreach (var thread in botThreads)
        {
            var action = agentActions.FirstOrDefault(a => a.ThreadId == thread.Id);

            // An agent action always wins over the heuristics: apply it, deduping the reply
            // when a previous attempt already posted the same text (retry after a crash).
            if (action is not null)
            {
                operations.Add(WithRetryDedupe(thread, action, replyTextForMatch));
                continue;
            }

            if (thread.HasPendingHumanReply)
            {
                operations.Add(new TriageOperation(thread.Id, TriageOp.None, null, null)); // flagged by caller as manual
                continue;
            }

            if (thread.Status == ReviewThreadStatus.Active
                && thread.DedupeKey is not null
                && !currentFindingKeys.Contains(thread.DedupeKey))
            {
                operations.Add(new TriageOperation(
                    thread.Id, TriageOp.Resolve,
                    "Resolved: this finding no longer reproduces in the latest iteration.",
                    ReviewThreadStatus.Fixed));
            }
        }

        return operations;
    }

    /// <summary>
    /// Retry-aware: when the previous attempt already posted this exact reply (last
    /// comment is the bot's, text matches), skip the duplicate reply but keep the
    /// pending status change.
    /// </summary>
    private static TriageOperation WithRetryDedupe(
        ReviewThread thread, ThreadAction action, Func<string, string> replyTextForMatch)
    {
        TriageOp op;
        ReviewThreadStatus? status;
        switch (action.Action)
        {
            case ThreadActionKind.Resolve:
                op = TriageOp.Resolve;
                status = ReviewThreadStatus.Fixed;
                break;
            case ThreadActionKind.Reopen:
                op = TriageOp.Reopen;
                status = ReviewThreadStatus.Active;
                break;
            default:
                op = TriageOp.Answer;
                status = null;
                break;
        }

        var alreadyPosted = thread.LastComment is { IsBot: true } last
                            && string.Equals(last.Text.Trim(), replyTextForMatch(action.Comment).Trim(), StringComparison.Ordinal);
        return new TriageOperation(thread.Id, op, alreadyPosted ? null : action.Comment, status);
    }

    /// <summary>Thread ids with a pending human reply the agent did not act on.</summary>
    public static IReadOnlyList<int> Unanswered(
        IReadOnlyList<ReviewThread> botThreads, IReadOnlyList<ThreadAction> agentActions)
        =>
        [
            .. botThreads
                .Where(t => t.HasPendingHumanReply && agentActions.All(a => a.ThreadId != t.Id))
                .Select(t => t.Id)
        ];
}