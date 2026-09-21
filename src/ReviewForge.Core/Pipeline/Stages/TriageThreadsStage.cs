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

        // A finding "still reproduces" when it was accepted this run, was posted by a prior run,
        // or was re-detected this run but dedupe-rejected. Auto-resolve is reserved for keys that
        // are absent from all three — never for a finding merely filtered out by dedupe.
        var currentKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);
        if (ctx.PriorRun is { } prior)
        {
            currentKeys.UnionWith(prior.FindingKeys);
        }
        currentKeys.UnionWith(ctx.Collector.RedetectedKeys);

        ctx.TriagePlan = ThreadTriage.Plan(botThreads, currentKeys, agentActions);
        ctx.UnansweredThreads = ThreadTriage.Unanswered(botThreads, agentActions);

        // Threads are independent; only reply-then-status per thread must stay sequential.
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = ctx.TriagePlan
            .Where(op => op.Op != TriageOp.None)
            .Select(async op =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!string.IsNullOrWhiteSpace(op.Comment))
                    {
                        await source.ReplyToThreadAsync(ctx.Pr, op.ThreadId, op.Comment, ct).ConfigureAwait(false);
                    }

                    if (op.NewStatus is { } status)
                    {
                        await source.SetThreadStatusAsync(ctx.Pr, op.ThreadId, status, ct).ConfigureAwait(false);
                    }

                    logger.LogInformation("thread {ThreadId}: {Op}", op.ThreadId, op.Op);
                }
                finally
                {
                    gate.Release();
                }
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var threadId in ctx.UnansweredThreads)
        {
            logger.LogWarning("thread {ThreadId} has a pending human reply the agent did not answer", threadId);
        }
    }
}