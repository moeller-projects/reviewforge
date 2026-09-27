using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 4: re-fetch threads (new comments may have arrived during preparation),
/// classify full vs follow-up, extract pending human replies on bot threads.
/// Consumes the refresh stage 3 started when it genuinely overlapped the clone window;
/// otherwise re-fetches exactly as before.
/// </summary>
public sealed class ClassifyRunStage(IPullRequestSource source) : IReviewStage
{
    public string Name => "classify-run";

    public int Order => 40;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Threads = await ResolveThreadsAsync(ctx, ct).ConfigureAwait(false);
        ctx.Kind = RunClassifier.Classify(ctx.PriorRun);
        ctx.PendingReplies = RunClassifier.PendingReplies(ctx.Threads);
    }

    private async Task<IReadOnlyList<ReviewThread>> ResolveThreadsAsync(ReviewContext ctx, CancellationToken ct)
    {
        // Freshness rule: the overlapped fetch must have been launched before the clone
        // finished — then it was in flight across the slow window and is no older than the
        // post-clone refetch would be. Any other shape (stale/missing stamp) takes the
        // original serial path. A faulted overlap task propagates, exactly like a faulted
        // re-fetch — never an engine fallback.
        if (ctx.PendingThreadsRefresh is { } refresh
            && ctx.RepoPreparedAt is { } preparedAt
            && refresh.StartedAt < preparedAt)
        {
            return await refresh.Task.ConfigureAwait(false);
        }

        return await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
    }
}