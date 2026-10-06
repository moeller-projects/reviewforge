using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class CollectCommentsStageTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Execute_includes_latest_eligible_human_and_applies_author_gate_and_cap()
    {
        var source = new FakePullRequestSource();
        source.Threads =
        [
            Thread(1, [Human("other", "old", Now.AddMinutes(-2)), Human("creator", new string('x', 1001), Now.AddMinutes(1))]),
            Thread(2, [Human("other", "not allowed", Now.AddMinutes(1))]),
            Thread(3, [new ThreadComment("bot", "bot", true, "finding", Now), Human("creator", "pending reply", Now.AddMinutes(1))], "finding-key"),
        ];
        var ctx = Context(source);

        await new CollectCommentsStage(source, new FakeFindingStore(), new HashSet<string>(), TimeProvider.System).ExecuteAsync(ctx, CancellationToken.None);

        var comments = ctx.Resolve!.ResolvableComments;
        Assert.Equal([1, 2, 3], comments.Select(c => c.ThreadId));
        var first = comments[0];
        Assert.Equal("creator", first.RequesterId);
        Assert.True(first.CommenterAllowed);
        Assert.Equal(1000, first.RequestText.Length);
        Assert.Equal(2, first.History.Count);
        Assert.False(comments[1].CommenterAllowed);
        Assert.True(comments[2].CommenterAllowed);
    }

    [Fact]
    public async Task Execute_excludes_closed_fixed_bot_last_old_watermark_and_commands()
    {
        var source = new FakePullRequestSource();
        source.Threads =
        [
            Thread(1, [Human("creator", "closed", Now.AddMinutes(1))], status: ReviewThreadStatus.Closed),
            Thread(2, [Human("creator", "fixed", Now.AddMinutes(1))], status: ReviewThreadStatus.Fixed),
            Thread(3, [Human("creator", "old", Now)], status: ReviewThreadStatus.Active),
            Thread(4, [Human("creator", "latest", Now.AddMinutes(1)), new ThreadComment("bot", "answer", true, "bot", Now.AddMinutes(2))]),
            Thread(5, [Human("creator", "/fixit this", Now.AddMinutes(1))]),
            Thread(6, [Human("creator", "/resolve now", Now.AddMinutes(1))]),
            Thread(7, [Human("creator", "eligible", Now.AddMinutes(1))])
        ];
        var ctx = Context(source);
        ctx.Resolve!.ResolveWatermark = Now;

        await new CollectCommentsStage(source, new FakeFindingStore(), new HashSet<string>(), TimeProvider.System).ExecuteAsync(ctx, CancellationToken.None);

        // "/resolve" is itself a resolution request and stays; "/fixit" belongs to auto-fix.
        Assert.Equal([6, 7], ctx.Resolve!.ResolvableComments.Select(c => c.ThreadId));
    }

    [Fact]
    public async Task Execute_propagates_a_failed_threads_refresh_instead_of_refetching()
    {
        var source = new FakePullRequestSource();
        var ctx = Context(source);
        ctx.PendingThreadsRefresh = new ThreadsRefreshOverlap(
            Task.FromException<IReadOnlyList<ReviewThread>>(new InvalidOperationException("ado down")),
            Now);

        // No silent fallback to a second fetch: the provider failure fails the run visibly.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CollectCommentsStage(source, new FakeFindingStore(), new HashSet<string>(), TimeProvider.System)
                .ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Execute_uses_explicit_allowed_commenters_instead_of_author_only()
    {
        var source = new FakePullRequestSource();
        source.Threads = [Thread(1, [Human("reviewer", "request", Now.AddMinutes(1))]), Thread(2, [Human("creator", "author request", Now.AddMinutes(1))])];
        var ctx = Context(source);


        await new CollectCommentsStage(source, new FakeFindingStore(), new HashSet<string>(["reviewer"]), TimeProvider.System).ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(["reviewer", "creator"], ctx.Resolve!.ResolvableComments.Select(comment => comment.RequesterId));
        Assert.True(ctx.Resolve!.ResolvableComments[0].CommenterAllowed);
        Assert.False(ctx.Resolve!.ResolvableComments[1].CommenterAllowed);
    }

    [Fact]
    public async Task Execute_skips_a_previously_acted_on_comment_but_includes_a_newer_reply()
    {
        var source = new FakePullRequestSource();
        source.Threads =
        [
            Thread(1, [Human("creator", "already answered", Now)]),
            Thread(2, [Human("creator", "first request", Now), Human("creator", "new request", Now.AddMinutes(3))]),
        ];
        var pr = new PrKey("o", "p", "r", 1);
        var store = new FakeFindingStore();
        await store.SaveResolveActionsAsync(pr, Guid.NewGuid(),
        [
            new ResolveAction(0, Guid.NewGuid(), 1, TriageVerdict.Question, ResolutionOutcome.Question,
                null, true, Now.AddMinutes(2))
        ], CancellationToken.None);
        var ctx = new ReviewContext(pr, Now) {PullRequest = source.Pr, Resolve = new ResolveState()};

        await new CollectCommentsStage(source, store, new HashSet<string>(), TimeProvider.System).ExecuteAsync(ctx, CancellationToken.None);

        var only = Assert.Single(ctx.Resolve!.ResolvableComments);
        Assert.Equal(2, only.ThreadId);
        Assert.Equal("new request", only.RequestText);
    }

    [Fact]
    public async Task Execute_retries_deferred_comment_despite_watermark_and_bot_reply()
    {
        var source = new FakePullRequestSource();
        source.Threads =
        [
            Thread(9, [
                new ThreadComment("bot", "bot", true, "original finding", Now.AddMinutes(-3)),
                Human("creator", "please fix this", Now.AddMinutes(-2)),
                new ThreadComment("bot", "bot", true, "Deferred to next resolve run", Now.AddMinutes(1)),
            ], "finding-key"),
        ];
        var pr = new PrKey("o", "p", "r", 1);
        var store = new FakeFindingStore();
        var run = Guid.NewGuid();
        await store.SaveResolveActionsAsync(pr, run,
        [
            new ResolveAction(0, run, 9, TriageVerdict.Actionable, ResolutionOutcome.Deferred,
                null, true, Now.AddMinutes(1))
        ], CancellationToken.None);
        var ctx = Context(source);
        ctx.Resolve!.ResolveWatermark = Now;

        await new CollectCommentsStage(source, store, new HashSet<string>(), TimeProvider.System).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(9, Assert.Single(ctx.Resolve!.ResolvableComments).ThreadId);
    }

    private static ReviewContext Context(FakePullRequestSource source)
        => new(new PrKey("o", "p", "r", 1), Now)
        {
            PullRequest = source.Pr with {CreatorId = "creator"},
            Resolve = new ResolveState(),
        };

    private static ThreadComment Human(string id, string text, DateTimeOffset at)
        => new(id, id, false, text, at);

    private static ReviewThread Thread(int id, IReadOnlyList<ThreadComment> comments, string? key = null, ReviewThreadStatus status = ReviewThreadStatus.Active)
        => new(id, key, status, comments, new ThreadAnchor("src/a.cs", 1, 1));
}