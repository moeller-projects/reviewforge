using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 8: plan and apply thread triage — answer/resolve/reopen per agent decision,
/// auto-resolve findings that no longer reproduce, flag unanswered threads for humans.
/// Guarded by the publish claim before any external write.
/// </summary>
public sealed class TriageThreadsStage(IPullRequestSource source, ILogger<TriageThreadsStage> logger) : IReviewStage
{
    public string Name => "triage-threads";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before triage");

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

        // P1-5: pass CommentFormatter.WithBotPreamble as replyTextForMatch once the preamble lands.
        ctx.TriagePlan = ThreadTriage.Plan(botThreads, currentKeys, agentActions);
        ctx.UnansweredThreads = ThreadTriage.Unanswered(botThreads, agentActions);

        // Threads are independent; only reply-then-status per thread must stay sequential.
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = ctx.TriagePlan
            .Where(op => op.Op != TriageOp.None)
            .Select(op => ApplyAsync(ctx, op, gate, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var threadId in ctx.UnansweredThreads)
        {
            logger.LogWarning("thread {ThreadId} has a pending human reply the agent did not answer", threadId);
        }
    }

    private async Task ApplyAsync(ReviewContext ctx, TriageOperation op, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before write on thread {op.ThreadId}");

            if (!string.IsNullOrWhiteSpace(op.Comment))
            {
                // P1-5: wrap with CommentFormatter.WithBotPreamble once the preamble lands.
                if (await AlreadyRepliedAsync(ctx, op.ThreadId, op.Comment, ct).ConfigureAwait(false))
                {
                    logger.LogInformation("thread {ThreadId}: reply already posted by a previous attempt — skipping", op.ThreadId);
                }
                else
                {
                    await source.ReplyToThreadAsync(ctx.Pr, op.ThreadId, op.Comment, ct).ConfigureAwait(false);
                }
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
    }

    /// <summary>
    /// Re-fetch the thread immediately before replying: a previous attempt or a
    /// competing run may have posted this exact reply after ctx.Threads was fetched.
    /// </summary>
    private async Task<bool> AlreadyRepliedAsync(ReviewContext ctx, int threadId, string text, CancellationToken ct)
    {
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        return threads.FirstOrDefault(t => t.Id == threadId)?.LastComment is { IsBot: true } last
               && string.Equals(last.Text.Trim(), text.Trim(), StringComparison.Ordinal);
    }
}
