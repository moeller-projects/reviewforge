using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class ResolveGateStage(IFindingStore store, IReadOnlySet<string> allowedAuthors, string? requestedHeadSha, ILogger<ResolveGateStage>? logger = null) : IReviewStage
{
    public string Name => "resolve-gate";
    public int Order => 20;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var pr = ctx.RequirePullRequest();
        if (!string.IsNullOrWhiteSpace(requestedHeadSha)
            && !string.Equals(requestedHeadSha, pr.SourceCommitSha, StringComparison.OrdinalIgnoreCase))
            throw new PrHeadChangedException(requestedHeadSha, pr.SourceCommitSha);
        var apiManual = ctx.RunKind == RunKind.Resolve && ctx.Trigger == EnqueueTrigger.Manual;
        var lastResolveRun = apiManual ? null : await store.GetLastCompletedResolveRunAsync(ctx.Pr, ct).ConfigureAwait(false);
        var priorActions = await store.GetResolveActionsAsync(ctx.Pr, [], ct).ConfigureAwait(false);
        var activeThreads = ctx.Threads
            .Where(thread => thread.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                             && thread.Comments.Any(comment => !comment.IsBot))
            .Select(thread => thread.Id).ToHashSet();
        var deferredThreadIds = priorActions
            .Where(action => action.Outcome == ResolutionOutcome.Deferred && activeThreads.Contains(action.ThreadId))
            .Select(action => action.ThreadId)
            .ToHashSet();
        ctx.ResolveWatermark = apiManual || deferredThreadIds.Count > 0
            ? null
            : lastResolveRun?.LastObservedCommentAt ?? lastResolveRun?.CompletedAt;
        var decision = ResolveGate.Evaluate(
            pr, ctx.Threads, ctx.ResolveWatermark, apiManual || allowedAuthors.Contains(pr.CreatorId), deferredThreadIds);
        if (decision != ResolveGateDecision.Continue)
        {
            logger?.LogInformation("resolve gate terminated run: {Decision}", decision);
            ctx.Terminate(decision.ToString());
        }
    }
}
