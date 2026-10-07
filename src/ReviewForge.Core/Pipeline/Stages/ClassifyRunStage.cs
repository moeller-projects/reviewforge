using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 4: re-fetch threads (new comments may have arrived during preparation),
/// classify full vs follow-up, extract pending human replies on bot threads.
/// Consumes the refresh stage 3 started only when the response demonstrably arrived
/// at/after preparation finished; otherwise — including a faulted overlap — re-fetches
/// exactly as before (the overlap is an optimization, never a correctness path).
/// </summary>
public sealed class ClassifyRunStage(IPullRequestSource source, TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "classify-run";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Fetch = ctx.Fetch with
        {
            Threads = await ResolveThreadsAsync(ctx, ct).ConfigureAwait(false)
        };
        ctx.Classification = ctx.Classification with
        {
            Kind = RunClassifier.Classify(ctx.Fetch.PriorRun),
            PendingReplies = RunClassifier.PendingReplies(ctx.Fetch.Threads),
        };
    }

    private async Task<IReadOnlyList<ReviewThread>> ResolveThreadsAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (ctx.Repository.PendingThreadsRefresh is { } refresh)
        {
            // In-flight must be sampled BEFORE the await: after it, the task is always
            // complete and "now" would wrongly stamp a long-finished (possibly stale)
            // response as fresh.
            var wasInFlight = !refresh.Task.IsCompleted;
            IReadOnlyList<ReviewThread>? overlapped = null;
            try
            {
                overlapped = await refresh.Task.ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A faulted overlap falls through to the serial refetch below — never an
                // engine fallback: the refetch surfaces its own failures exactly as the
                // pre-overlap pipeline did.
            }

            // Freshness rule: consume the overlapped response only when it was RECEIVED at
            // or after preparation finished — then it is at least as fresh as the
            // post-clone refetch it replaces. "Already complete but unstamped" is
            // unproven (the stamping continuation may not have run yet), so it re-fetches.
            // A genuinely in-flight fetch awaited here completes "now", necessarily
            // at/after preparation.
            var completedAt = refresh.CompletedAt ?? (wasInFlight ? _Clock.GetUtcNow() : null);
            if (overlapped is not null && completedAt is { } receivedAt
                                       && ctx.Repository.RepoPreparedAt is { } preparedAt && receivedAt >= preparedAt)
            {
                return overlapped;
            }
        }

        return await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
    }
}