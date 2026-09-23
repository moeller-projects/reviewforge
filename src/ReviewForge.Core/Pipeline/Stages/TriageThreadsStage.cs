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

    public int Order => 80;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before triage");

        var botThreads = ctx.Threads.Where(t => t.DedupeKey is not null).ToList();
        var agentActions = ctx.RequireResult().Narrative.ThreadActions ?? [];

        // Only findings validated in this run count as current. Prior keys remain in
        // persistence for deduplication, but must not keep stale bot threads alive.
        var currentKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);
        currentKeys.UnionWith(ctx.Collector.RedetectedKeys);

        var current = await source.GetPullRequestAsync(ctx.Pr, ct).ConfigureAwait(false);
        var reviewed = ctx.RequirePullRequest().SourceCommitSha;
        if (!string.Equals(current.SourceCommitSha, reviewed, StringComparison.OrdinalIgnoreCase))
        {
            throw new PrHeadChangedException(reviewed, current.SourceCommitSha);
        }

        ctx.TriagePlan = ThreadTriage.Plan(botThreads, currentKeys, agentActions, CommentFormatter.WithBotPreamble);
        ctx.UnansweredThreads = ThreadTriage.Unanswered(botThreads, agentActions);
        var opCount = ctx.TriagePlan.Count(o => o.Op != TriageOp.None);
        logger.LogInformation("triage plan: {BotThreads} bot threads, {Actions} agent actions, {Ops} ops, {Unanswered} unanswered", botThreads.Count, agentActions.Length, opCount, ctx.UnansweredThreads.Count);

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
                var text = CommentFormatter.WithBotPreamble(op.Comment);
                if (await AlreadyRepliedAsync(ctx, op.ThreadId, text, ct).ConfigureAwait(false))
                {
                    logger.LogInformation("thread {ThreadId}: reply already posted by a previous attempt — skipping", op.ThreadId);
                }
                else
                {
                    await source.ReplyToThreadAsync(ctx.Pr, op.ThreadId, text, ct).ConfigureAwait(false);
                    ReviewForgeTelemetry.ThreadsReplied.Add(1);
                }
            }
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before status write on thread {op.ThreadId}");

            if (op.NewStatus is { } status)
            {
                await source.SetThreadStatusAsync(ctx.Pr, op.ThreadId, status, ct).ConfigureAwait(false);
                if (status == ReviewThreadStatus.Fixed)
                {
                    ReviewForgeTelemetry.ThreadsResolved.Add(1);
                }
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
