using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 8: plan and apply thread triage — answer/resolve/reopen per agent decision,
/// auto-resolve findings that no longer reproduce, flag unanswered threads for humans.
/// </summary>
public sealed class TriageThreadsStage(IPullRequestSource source, ILogger<TriageThreadsStage> logger) : IReviewStage
{
    public string Name => "triage-threads";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var botThreads = ctx.Threads.Where(t => t.DedupeKey is not null).ToList();
        var agentActions = ctx.Result!.Narrative.ThreadActions ?? [];
        var currentKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);

        ctx.TriagePlan = ThreadTriage.Plan(botThreads, currentKeys, agentActions);
        ctx.UnansweredThreads = ThreadTriage.Unanswered(botThreads, agentActions);

        foreach (var op in ctx.TriagePlan)
        {
            if (op.Op == TriageOp.None)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(op.Comment))
            {
                await source.ReplyToThreadAsync(ctx.Pr, op.ThreadId, op.Comment, ct);
            }

            if (op.NewStatus is { } status)
            {
                await source.SetThreadStatusAsync(ctx.Pr, op.ThreadId, status, ct);
            }

            logger.LogInformation("thread {ThreadId}: {Op}", op.ThreadId, op.Op);
        }

        foreach (var threadId in ctx.UnansweredThreads)
        {
            logger.LogWarning("thread {ThreadId} has a pending human reply the agent did not answer", threadId);
        }
    }
}