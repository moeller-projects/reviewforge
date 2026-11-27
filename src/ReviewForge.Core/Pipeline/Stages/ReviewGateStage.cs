using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 2: decide whether the PR requires a review; terminate gracefully when not.</summary>
public sealed class ReviewGateStage(TimeProvider? clock = null, ILogger<ReviewGateStage>? logger = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "review-gate";


    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var pr = ctx.RequirePullRequest();
        // An explicit read-only preview is useful even for draft PRs and already-reviewed
        // heads. Ordinary/discovered reviews retain their gates; this grants no publish,
        // fix, vote, or coverage privileges to the draft pipeline.
        ctx.Gate = ctx.RunKind == RunKind.ReviewDraft && ctx.Trigger == EnqueueTrigger.Manual
            ? GateDecision.Review()
            : ReviewGate.Evaluate(pr, ctx.Fetch.PriorRun, ctx.Fetch.Threads, _Clock.GetUtcNow(),
                forceReview: ctx.ForceReview && ctx.RunKind == RunKind.Review && ctx.Trigger == EnqueueTrigger.Manual);
        logger?.LogDebug("gate decision for {Pr}: shouldReview={ShouldReview}, reason={Reason}, hasPriorRun={HasPriorRun}, threadCount={ThreadCount}",
            ctx.Pr, ctx.Gate.ShouldReview, ctx.Gate.Reason, ctx.Fetch.PriorRun is not null, ctx.Fetch.Threads.Count);
        if (!ctx.Gate.ShouldReview)
        {
            ctx.Terminate(ctx.Gate.Reason);
        }

        return Task.CompletedTask;
    }
}