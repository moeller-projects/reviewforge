using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ManualDraftGateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Explicit_manual_read_only_drafts_review_draft_prs_and_previously_reviewed_heads(
        bool draftPr, bool previouslyReviewed)
    {
        using var ctx = Context(RunKind.ReviewDraft, EnqueueTrigger.Manual, draftPr, previouslyReviewed);

        await new ReviewGateStage().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(ctx.Gate!.ShouldReview);
        Assert.False(ctx.Terminated);
        Assert.False(ctx.ForceReview);
    }

    [Theory]
    [InlineData(RunKind.Review, EnqueueTrigger.Manual, true, false)]
    [InlineData(RunKind.Review, EnqueueTrigger.Manual, false, true)]
    [InlineData(RunKind.Review, EnqueueTrigger.Discovery, true, false)]
    [InlineData(RunKind.ReviewDraft, EnqueueTrigger.Discovery, true, false)]
    [InlineData(RunKind.ReviewDraft, EnqueueTrigger.Discovery, false, true)]
    [InlineData(RunKind.Resolve, EnqueueTrigger.Manual, true, false)]
    public async Task Other_run_kinds_and_non_manual_drafts_retain_the_shared_gate(
        RunKind kind, EnqueueTrigger trigger, bool draftPr, bool previouslyReviewed)
    {
        using var ctx = Context(kind, trigger, draftPr, previouslyReviewed);

        await new ReviewGateStage().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.False(ctx.Gate!.ShouldReview);
        Assert.True(ctx.Terminated);
        Assert.Equal(draftPr ? "PR is a draft" : "head already reviewed and no new human comments",
            ctx.TerminationReason);
    }

    [Fact]
    public async Task Manual_draft_still_requires_fetched_pr_context()
    {
        using var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            RunKind = RunKind.ReviewDraft,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReviewGateStage().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
    }

    private static ReviewContext Context(RunKind kind, EnqueueTrigger trigger, bool draftPr, bool previouslyReviewed)
    {
        var key = new PrKey("o", "p", "r", 1);
        return new ReviewContext(key, DateTimeOffset.UtcNow)
        {
            RunKind = kind,
            Trigger = trigger,
            Fetch = new FetchOutcome
            {
                PullRequest = new PullRequest(1, "title", null, "head", "base", "url", draftPr, "author", "Author"),
                PriorRun = previouslyReviewed ? new PriorRun(key, "head", DateTimeOffset.UtcNow, []) : null,
            },
        };
    }
}
