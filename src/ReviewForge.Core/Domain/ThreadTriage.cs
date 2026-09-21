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
    {
        var operations = new List<TriageOperation>();

        foreach (var thread in botThreads)
        {
            var action = agentActions.FirstOrDefault(a => a.ThreadId == thread.Id);

            if (thread.HasPendingHumanReply)
            {
                operations.Add(action is null
                    ? new TriageOperation(thread.Id, TriageOp.None, null, null) // flagged by caller as manual
                    : action.Action switch
                    {
                        ThreadActionKind.Resolve => new(thread.Id, TriageOp.Resolve, action.Comment, ReviewThreadStatus.Fixed),
                        ThreadActionKind.Reopen => new(thread.Id, TriageOp.Reopen, action.Comment, ReviewThreadStatus.Active),
                        _ => new(thread.Id, TriageOp.Answer, action.Comment, null),
                    });
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