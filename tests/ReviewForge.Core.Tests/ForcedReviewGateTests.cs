using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ForcedReviewGateTests
{
    [Theory]
    [InlineData(true, false, RunKind.Review, EnqueueTrigger.Manual, true)]
    [InlineData(false, false, RunKind.Review, EnqueueTrigger.Manual, false)]
    [InlineData(true, true, RunKind.Review, EnqueueTrigger.Manual, false)]
    [InlineData(true, false, RunKind.Review, EnqueueTrigger.Discovery, false)]
    [InlineData(true, false, RunKind.Review, EnqueueTrigger.ResolveCommand, false)]
    [InlineData(true, false, RunKind.ReviewDraft, EnqueueTrigger.Manual, false)]
    [InlineData(true, false, RunKind.Resolve, EnqueueTrigger.Manual, false)]
    public async Task Only_manual_ordinary_reviews_can_force_the_same_head_gate(
        bool force, bool draft, RunKind kind, EnqueueTrigger trigger, bool shouldReview)
    {
        var key = new PrKey("o", "p", "r", 1);
        using var ctx = new ReviewContext(key, DateTimeOffset.UtcNow)
        {
            ForceReview = force, RunKind = kind, Trigger = trigger,
            Fetch = new FetchOutcome
            {
                PullRequest = new PullRequest(1, "title", null, "head", "base", "url", draft, "author", "Author"),
                PriorRun = new PriorRun(key, "head", DateTimeOffset.UtcNow, []),
            },
        };
        await new ReviewGateStage().ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(shouldReview, ctx.Gate!.ShouldReview);
        Assert.Equal(!shouldReview, ctx.Terminated);
    }
}
