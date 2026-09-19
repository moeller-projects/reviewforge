using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 2: decide whether the PR requires a review; terminate gracefully when not.</summary>
public sealed class ReviewGateStage(TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "review-gate";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Gate = ReviewGate.Evaluate(ctx.PullRequest!, ctx.PriorRun, ctx.Threads, _Clock.GetUtcNow());
        if (!ctx.Gate.ShouldReview)
        {
            ctx.Terminate(ctx.Gate.Reason);
        }

        return Task.CompletedTask;
    }
}