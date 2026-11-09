using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Resolve;
using ReviewForge.Core.Resolve.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Resolve.Stages;

public sealed class ResolveGateStage(
    IFindingStore store,
    IReadOnlySet<string> allowedAuthors,
    ILogger<ResolveGateStage>? logger = null,
    bool requireCommand = false) : IReviewStage
{
    public string Name => "resolve-gate";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var resolve = ctx.RequireResolveState();
        var pr = ctx.RequirePullRequest();
        if (!string.IsNullOrWhiteSpace(resolve.RequestedHeadSha)
            && !string.Equals(resolve.RequestedHeadSha, pr.SourceCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new PrHeadChangedException(resolve.RequestedHeadSha, pr.SourceCommitSha);
        var apiManual = ctx.RunKind == RunKind.Resolve && ctx.Trigger == EnqueueTrigger.Manual;
        var lastResolveRun = apiManual ? null : await store.GetLastCompletedResolveRunAsync(ctx.Pr, ct).ConfigureAwait(false);
        var priorActions = await store.GetResolveActionsAsync(ctx.Pr, [], ct).ConfigureAwait(false);
        var activeThreads = ctx.Fetch.Threads
            .Where(thread => thread.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                             && thread.Comments.Any(comment => comment.IsHuman))
            .Select(thread => thread.Id).ToHashSet();
        var deferredThreadIds = priorActions
            .Where(action => action.Outcome == ResolutionOutcome.Deferred && activeThreads.Contains(action.ThreadId))
            .Select(action => action.ThreadId)
            .ToHashSet();
        resolve.ResolveWatermark = apiManual || deferredThreadIds.Count > 0
            ? null
            : lastResolveRun?.LastObservedCommentAt ?? lastResolveRun?.CompletedAt;
        logger?.LogDebug("resolve gate inputs: threads={ThreadCount}, active={ActiveThreadCount}, deferred={DeferredThreadCount}, manual={Manual}, watermarkPresent={WatermarkPresent}",
            ctx.Fetch.Threads.Count, activeThreads.Count, deferredThreadIds.Count, apiManual, resolve.ResolveWatermark is not null);
        var decision = ResolveGate.Evaluate(
            pr, ctx.Fetch.Threads, resolve.ResolveWatermark,
            apiManual || allowedAuthors.Contains(pr.CreatorId), deferredThreadIds,
            requireCommand, pr.CreatorId);
        logger?.LogDebug("resolve gate decision: {Decision}", decision);
        if (decision != ResolveGateDecision.Continue)
        {
            logger?.LogInformation("resolve gate terminated run: {Decision}", decision);
            ctx.Terminate(decision.ToString());
        }
    }
}