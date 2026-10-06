using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ResolveGateTests
{
    private static readonly DateTimeOffset Watermark = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_prioritizes_draft_over_other_gates()
    {
        var pr = Pr() with { IsDraft = true };
        var result = ResolveGate.Evaluate(pr, [Thread(1, Human("creator", Watermark.AddDays(1)))], Watermark, authorAllowed: false);
        Assert.Equal(ResolveGateDecision.Draft, result);
    }

    [Fact]
    public void Evaluate_rejects_non_allowlisted_author()
    {
        var result = ResolveGate.Evaluate(Pr(), [Thread(1, Human("creator", Watermark.AddDays(1)))], Watermark, authorAllowed: false);
        Assert.Equal(ResolveGateDecision.AuthorNotAllowed, result);
    }

    [Fact]
    public void Evaluate_ignores_bot_comments_when_deciding_whether_comments_exist()
    {
        var result = ResolveGate.Evaluate(Pr(), [Thread(1, new ThreadComment("bot", "bot", true, "finding", Watermark.AddDays(1)))], Watermark, true);
        Assert.Equal(ResolveGateDecision.NoComments, result);
    }

    [Fact]
    public void Evaluate_requires_a_human_comment_newer_than_watermark()
    {
        var thread = Thread(1, Human("creator", Watermark));
        Assert.Equal(ResolveGateDecision.NoNewComments, ResolveGate.Evaluate(Pr(), [thread], Watermark, true));
        Assert.Equal(ResolveGateDecision.Continue, ResolveGate.Evaluate(Pr(), [Thread(1, Human("creator", Watermark.AddTicks(1)))], Watermark, true));
    }

    [Fact]
    public void Evaluate_ignores_new_comments_on_closed_or_fixed_threads()
    {
        // The collector excludes these threads; the gate must not continue for them either,
        // or the run persists a no-work completion instead of terminating here.
        var thread = new ReviewThread(1, null, ReviewThreadStatus.Fixed, [Human("creator", Watermark.AddDays(1))]);
        Assert.Equal(ResolveGateDecision.NoNewComments, ResolveGate.Evaluate(Pr(), [thread], Watermark, true));
    }

    [Fact]
    public void Evaluate_ignores_threads_whose_last_comment_is_the_bots()
    {
        var thread = new ReviewThread(1, null, ReviewThreadStatus.Active,
        [
            Human("creator", Watermark.AddDays(1)),
            new ThreadComment("bot", "bot", true, "answer", Watermark.AddDays(2)),
        ]);
        Assert.Equal(ResolveGateDecision.NoNewComments, ResolveGate.Evaluate(Pr(), [thread], Watermark, true));
    }

    [Fact]
    public void Evaluate_retries_deferred_bot_last_threads()
    {
        var thread = new ReviewThread(1, null, ReviewThreadStatus.Active,
        [
            Human("creator", Watermark.AddDays(-1)),
            new ThreadComment("bot", "bot", true, "answer", Watermark),
        ]);
        Assert.Equal(
            ResolveGateDecision.Continue,
            ResolveGate.Evaluate(Pr(), [thread], Watermark, true, deferredThreadIds: new HashSet<int> { 1 }));
    }

    [Fact]
    public async Task Stage_rejects_a_stale_requested_head_before_loading_watermark()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            PullRequest = Pr() with { SourceCommitSha = "current" },
            Threads = [Thread(1, Human("creator", DateTimeOffset.UtcNow))]
        };

        await Assert.ThrowsAsync<PrHeadChangedException>(() =>
            new ResolveGateStage(new FakeFindingStore(), new HashSet<string>(["creator"]), "requested", null)
                .ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Stage_allows_explicit_api_manual_run_without_allowlist_or_watermark()
    {
        var store = new FakeFindingStore();
        await store.SaveRunAsync(new ReviewRun(
            Guid.NewGuid(), new PrKey("o", "p", "r", 1), "head", ReviewKind.Full,
            Watermark, Watermark.AddMinutes(1), true, [],
            LastObservedCommentAt: Watermark, Pipeline: "Resolve"), CancellationToken.None);
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), Watermark.AddDays(1))
        {
            PullRequest = Pr(),
            Threads = [Thread(1, Human("reviewer", Watermark))],
            RunKind = RunKind.Resolve,
            Trigger = EnqueueTrigger.Manual,
        };

        await new ResolveGateStage(store, new HashSet<string>(), requestedHeadSha: null)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.TerminationReason);
        Assert.Null(ctx.ResolveWatermark);
    }

    [Fact]
    public async Task Stage_reopens_watermark_for_deferred_actions_without_bypassing_author_gate()
    {
        var pr = new PrKey("o", "p", "r", 1);
        var store = new FakeFindingStore();
        var run = Guid.NewGuid();
        await store.SaveRunAsync(new ReviewRun(run, pr, "head", ReviewKind.Full,
            Watermark.AddMinutes(-1), Watermark.AddMinutes(1), true, [],
            LastObservedCommentAt: Watermark, Pipeline: "Resolve"), CancellationToken.None);
        await store.SaveResolveActionsAsync(pr, run,
            [new ResolveAction(0, run, 1, TriageVerdict.Actionable, ResolutionOutcome.Deferred,
                null, true, Watermark.AddMinutes(1))], CancellationToken.None);
        var ctx = new ReviewContext(pr, Watermark.AddDays(1))
        {
            PullRequest = Pr(),
            Threads = [Thread(1, Human("creator", Watermark.AddMinutes(-1)))],
            RunKind = RunKind.Resolve,
            Trigger = EnqueueTrigger.Discovery,
        };

        await new ResolveGateStage(store, new HashSet<string>(["creator"], StringComparer.OrdinalIgnoreCase), null)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.TerminationReason);
        Assert.Null(ctx.ResolveWatermark);
    }

    [Fact]
    public async Task Stage_terminates_when_creator_is_not_allowlisted()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), Watermark)
        {
            PullRequest = Pr(),
            Threads = [Thread(1, Human("creator", Watermark.AddDays(1)))],
            RunKind = RunKind.Resolve,
            Trigger = EnqueueTrigger.Discovery,
        };
        var stage = new ResolveGateStage(new FakeFindingStore(), new HashSet<string>(), requestedHeadSha: null);

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("resolve-gate", stage.Name);
        Assert.Equal(ResolveGateDecision.AuthorNotAllowed.ToString(), ctx.TerminationReason);
    }

    private static PullRequest Pr() => new(1, "title", null, "head", "base", "clone", false, "creator", "Creator");
    private static ThreadComment Human(string id, DateTimeOffset at) => new(id, id, false, "please fix", at);
    private static ReviewThread Thread(int id, ThreadComment comment) => new(id, null, ReviewThreadStatus.Active, [comment]);
}
