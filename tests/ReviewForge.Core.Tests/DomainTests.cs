using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public class DomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static PullRequest Pr(bool draft = false, string head = "sha-1")
        => new(7, "t", null, head, "base", "url", draft);

    private static ReviewThread HumanThread(DateTimeOffset when)
        => new(1, null, ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", IsBot: false, "ping", when)]);

    [Fact]
    public void Gate_skips_drafts()
    {
        var decision = ReviewGate.Evaluate(Pr(draft: true), null, [], Now);
        Assert.False(decision.ShouldReview);
        Assert.Equal("PR is a draft", decision.Reason);
    }

    [Fact]
    public void Gate_reviews_when_no_prior_run()
    {
        Assert.True(ReviewGate.Evaluate(Pr(), null, [], Now).ShouldReview);
    }

    [Fact]
    public void Gate_reviews_when_head_changed()
    {
        var prior = new PriorRun(new PrKey("o", "p", "r", 7), "old-sha", Now.AddHours(-1), []);
        Assert.True(ReviewGate.Evaluate(Pr(head: "new-sha"), prior, [], Now).ShouldReview);
    }

    [Fact]
    public void Gate_skips_same_head_without_new_human_comments()
    {
        var prior = new PriorRun(new PrKey("o", "p", "r", 7), "sha-1", Now.AddHours(-1), []);
        var decision = ReviewGate.Evaluate(Pr(), prior, [HumanThread(Now.AddHours(-2))], Now);
        Assert.False(decision.ShouldReview);
        Assert.Contains("no new human comments", decision.Reason);
    }

    [Fact]
    public void Gate_reviews_same_head_with_new_human_comment()
    {
        var prior = new PriorRun(new PrKey("o", "p", "r", 7), "sha-1", Now.AddHours(-1), []);
        Assert.True(ReviewGate.Evaluate(Pr(), prior, [HumanThread(Now)], Now).ShouldReview);
    }

    [Fact]
    public void Gate_ignores_new_bot_comments()
    {
        var prior = new PriorRun(new PrKey("o", "p", "r", 7), "sha-1", Now.AddHours(-1), []);
        var botThread = new ReviewThread(1, "k", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", IsBot: true, "note", Now)]);
        Assert.False(ReviewGate.Evaluate(Pr(), prior, [botThread], Now).ShouldReview);
    }

    [Fact]
    public void GateDecision_factories()
    {
        Assert.True(GateDecision.Review().ShouldReview);
        var skip = GateDecision.Skip("why");
        Assert.False(skip.ShouldReview);
        Assert.Equal("why", skip.Reason);
    }

    [Fact]
    public void Classifier_full_without_prior_followup_with_prior()
    {
        Assert.Equal(ReviewKind.Full, RunClassifier.Classify(null));
        Assert.Equal(ReviewKind.FollowUp,
            RunClassifier.Classify(new PriorRun(new PrKey("o", "p", "r", 7), "s", Now, [])));
    }

    [Fact]
    public void PendingReplies_only_bot_threads_with_human_last_comment()
    {
        var pending = new ReviewThread(1, "key", ReviewThreadStatus.Active,
        [
            new ThreadComment("b", "bot", true, "finding", Now.AddHours(-2)),
            new ThreadComment("u", "human", false, "why?", Now.AddHours(-1)),
        ]);
        var answered = new ReviewThread(2, "key2", ReviewThreadStatus.Active,
        [
            new ThreadComment("u", "human", false, "why?", Now.AddHours(-2)),
            new ThreadComment("b", "bot", true, "because", Now.AddHours(-1)),
        ]);
        var humanThread = new ReviewThread(3, null, ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", false, "general", Now)]);

        var replies = RunClassifier.PendingReplies([pending, answered, humanThread]);

        var reply = Assert.Single(replies);
        Assert.Equal(1, reply.ThreadId);
        Assert.Equal("key", reply.DedupeKey);
        Assert.Equal("human", reply.Author);
        Assert.Equal("why?", reply.Text);
    }

    [Fact]
    public void ReviewThread_helpers_handle_empty_and_ordering()
    {
        var empty = new ReviewThread(1, null, ReviewThreadStatus.Active, []);
        Assert.Null(empty.LastComment);
        Assert.False(empty.HasPendingHumanReply);

        var botLast = new ReviewThread(2, null, ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "x", Now)]);
        Assert.False(botLast.HasPendingHumanReply);
        Assert.Equal("x", botLast.LastComment!.Text);
    }

    [Fact]
    public void PrKey_ToString_roundtrip_format()
        => Assert.Equal("org/proj/repo/42", new PrKey("org", "proj", "repo", 42).ToString());
}

public class ThreadTriageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static ReviewThread BotThread(int id, string key, ReviewThreadStatus status, bool humanLast)
        => new(id, key, status,
        [
            new ThreadComment("b", "bot", true, "finding", T0),
            .. humanLast ? new[] {new ThreadComment("u", "human", false, "reply", T0.AddMinutes(1))} : Array.Empty<ThreadComment>(),
        ]);

    [Fact]
    public void Pending_reply_with_resolve_action_resolves_fixed()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(1, "k", ReviewThreadStatus.Active, humanLast: true)],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Resolve, "fixed in latest push")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Resolve, op.Op);
        Assert.Equal(ReviewThreadStatus.Fixed, op.NewStatus);
        Assert.Equal("fixed in latest push", op.Comment);
    }

    [Fact]
    public void Pending_reply_with_reopen_action_reopens_active()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(1, "k", ReviewThreadStatus.Fixed, humanLast: true)],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Reopen, "still broken, see line 12")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Reopen, op.Op);
        Assert.Equal(ReviewThreadStatus.Active, op.NewStatus);
    }

    [Fact]
    public void Pending_reply_with_answer_action_keeps_status()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(1, "k", ReviewThreadStatus.Active, humanLast: true)],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Answer, "good question — because X")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Answer, op.Op);
        Assert.Null(op.NewStatus);
    }

    [Fact]
    public void Pending_reply_without_action_is_flagged_manual()
    {
        var threads = new[] {BotThread(9, "k", ReviewThreadStatus.Active, humanLast: true)};
        var plan = ThreadTriage.Plan(threads, ["k"], []);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.None, op.Op);
        Assert.Equal([9], ThreadTriage.Unanswered(threads, []));
    }

    [Fact]
    public void Active_thread_whose_finding_vanished_is_auto_resolved()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(3, "stale-key", ReviewThreadStatus.Active, humanLast: false)],
            ["other-key"],
            []);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Resolve, op.Op);
        Assert.Equal(ReviewThreadStatus.Fixed, op.NewStatus);
        Assert.Contains("no longer reproduces", op.Comment);
    }

    [Fact]
    public void Active_thread_whose_finding_still_reproduces_is_untouched()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(3, "k", ReviewThreadStatus.Active, humanLast: false)],
            ["k"],
            []);
        Assert.Empty(plan);
    }

    [Fact]
    public void Fixed_threads_are_untouched()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(3, "gone", ReviewThreadStatus.Fixed, humanLast: false)],
            [],
            []);
        Assert.Empty(plan);
    }

    [Fact]
    public void Unanswered_empty_when_agent_acted()
    {
        var threads = new[] {BotThread(5, "k", ReviewThreadStatus.Active, humanLast: true)};
        Assert.Empty(ThreadTriage.Unanswered(threads, [new ThreadAction(5, ThreadActionKind.Answer, "a")]));
    }

    private static ReviewThread ThreadWithBotReply(string botReply)
        => new(1, "k", ReviewThreadStatus.Active,
        [
            new ThreadComment("b", "bot", true, "finding", T0),
            new ThreadComment("u", "human", false, "why?", T0.AddMinutes(1)),
            new ThreadComment("b", "bot", true, botReply, T0.AddMinutes(2)),
        ]);

    [Fact]
    public void Plan_skips_reply_when_last_comment_is_bot_with_same_text()
    {
        var plan = ThreadTriage.Plan(
            [ThreadWithBotReply("because X")],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Answer, "because X")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Answer, op.Op);
        Assert.Null(op.Comment);
        Assert.Null(op.NewStatus);
    }

    [Fact]
    public void Plan_keeps_status_change_when_reply_already_posted()
    {
        var plan = ThreadTriage.Plan(
            [ThreadWithBotReply("fixed in latest push")],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Resolve, "fixed in latest push")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Resolve, op.Op);
        Assert.Null(op.Comment);
        Assert.Equal(ReviewThreadStatus.Fixed, op.NewStatus);
    }

    [Fact]
    public void Plan_posts_reply_when_last_bot_comment_differs()
    {
        var plan = ThreadTriage.Plan(
            [ThreadWithBotReply("something else")],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Answer, "because X")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Answer, op.Op);
        Assert.Equal("because X", op.Comment);
    }

    [Fact]
    public void Plan_posts_reply_when_last_comment_is_human()
    {
        var plan = ThreadTriage.Plan(
            [BotThread(1, "k", ReviewThreadStatus.Active, humanLast: true)],
            ["k"],
            [new ThreadAction(1, ThreadActionKind.Answer, "because X")]);

        var op = Assert.Single(plan);
        Assert.Equal(TriageOp.Answer, op.Op);
        Assert.Equal("because X", op.Comment);
    }
}

public class FailureBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly FailureBackoffPolicy Policy = new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8));

    private static ReviewRun Failed(string head, DateTimeOffset at)
        => new(Guid.NewGuid(), new PrKey("o", "p", "r", 7), head, ReviewKind.Full, at.AddMinutes(-1), at, false, []);

    private static ReviewRun Succeeded(string head, DateTimeOffset at)
        => new(Guid.NewGuid(), new PrKey("o", "p", "r", 7), head, ReviewKind.Full, at.AddMinutes(-1), at, true, []);

    [Fact]
    public void BlockedUntil_returns_null_with_no_failures()
        => Assert.Null(FailureBackoff.BlockedUntil([], "head", Now, Policy));

    [Fact]
    public void BlockedUntil_after_success_resets_streak()
    {
        // Newest first: success at -5 min, failure at -30 min.
        var runs = new[] {Succeeded("head", Now.AddMinutes(-5)), Failed("head", Now.AddMinutes(-30))};
        Assert.Null(FailureBackoff.BlockedUntil(runs, "head", Now, Policy));
    }

    [Fact]
    public void BlockedUntil_for_different_head()
    {
        var runs = new[] {Failed("other-head", Now.AddMinutes(-5))};
        Assert.Null(FailureBackoff.BlockedUntil(runs, "head", Now, Policy));
    }

    [Fact]
    public void BlockedUntil_doubles_per_consecutive_failure()
    {
        var t = Now.AddMinutes(-5);
        var one = FailureBackoff.BlockedUntil([Failed("head", t)], "head", Now, Policy);
        Assert.Equal(t + TimeSpan.FromMinutes(30), one);

        var two = FailureBackoff.BlockedUntil([Failed("head", t), Failed("head", t)], "head", Now, Policy);
        Assert.Equal(t + TimeSpan.FromHours(1), two);

        var three = FailureBackoff.BlockedUntil([Failed("head", t), Failed("head", t), Failed("head", t)], "head", Now, Policy);
        Assert.Equal(t + TimeSpan.FromHours(2), three);
    }

    [Fact]
    public void BlockedUntil_caps_at_max()
    {
        var t = Now.AddMinutes(-1);
        var runs = Enumerable.Range(0, 20).Select(_ => Failed("head", t)).ToArray();
        Assert.Equal(t + Policy.Max, FailureBackoff.BlockedUntil(runs, "head", Now, Policy));
    }

    [Fact]
    public void BlockedUntil_returns_null_once_window_elapsed()
    {
        var runs = new[] {Failed("head", Now.AddHours(-9))};
        Assert.Null(FailureBackoff.BlockedUntil(runs, "head", Now, Policy));
    }
}