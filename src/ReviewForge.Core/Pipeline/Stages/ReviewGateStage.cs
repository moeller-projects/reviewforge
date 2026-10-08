using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 2: decide whether the PR requires a review; terminate gracefully when not.</summary>
public sealed class ReviewGateStage(TimeProvider? clock = null, ILogger<ReviewGateStage>? logger = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "review-gate";


    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Gate = ReviewGate.Evaluate(ctx.RequirePullRequest(), ctx.Fetch.PriorRun, ctx.Fetch.Threads, _Clock.GetUtcNow());
        logger?.LogDebug("gate decision for {Pr}: shouldReview={ShouldReview}, reason={Reason}, hasPriorRun={HasPriorRun}, threadCount={ThreadCount}",
            ctx.Pr, ctx.Gate.ShouldReview, ctx.Gate.Reason, ctx.Fetch.PriorRun is not null, ctx.Fetch.Threads.Count);
        if (!ctx.Gate.ShouldReview)
        {
            ctx.Terminate(ctx.Gate.Reason);
        }

        return Task.CompletedTask;
    }
}