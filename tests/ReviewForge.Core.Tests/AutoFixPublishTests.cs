using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class AutoFixPublishTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    private static RichFinding Fixable(string dedupeKey = "k1")
        => new()
        {
            RuleId = "bash.unquoted-vars", Title = "Quote the variable", Severity = "medium",
            Category = "bug", Description = "The variable should be quoted.",
            Anchor = new FindingAnchor("script.sh", 5, 5), DedupeKey = dedupeKey,
            AppliedFix = new AppliedFix(
                dedupeKey,
                new FixProposal(
                    "script.sh", 3, 3,
                    "echo \"$name\"",
                    "Quoting prevents word-splitting on the value.",
                    FixOrigin.Deterministic),
                "none"),
        };

    private static ReviewContext Ctx(FakePullRequestSource source)
        => new(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = source.Pr,
            CurrentUser = source.User,
            Threads = source.Threads,
            Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []},
        };

    [Fact]
    public async Task Fixed_finding_posts_new_thread_anchored_at_the_fix_range()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.AcceptedFindings = [Fixable()];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var (finding, _) = Assert.Single(source.PostedFindings);
        Assert.Equal("script.sh", finding.Anchor!.FilePath);
        Assert.Equal(3, finding.Anchor.StartLine); // moved from the finding anchor (5,5) to the fix range
        Assert.Equal(3, finding.Anchor.EndLine);
        var body = Assert.Single(source.PostedFindingBodies);
        Assert.Contains("### 🔧 Quote the variable", body);
        Assert.Contains("**Fix available** — Quoting prevents word-splitting on the value.", body);
        Assert.Contains("```suggestion\necho \"$name\"\n```", body);
        // The finding keeps its fix attribution after publishing (persistence reads it).
        Assert.NotNull(finding.AppliedFix);
    }

    [Fact]
    public async Task Fixed_finding_with_live_thread_replies_with_the_fix_instead_of_suppressing()
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(7, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "old finding", DateTimeOffset.UtcNow)]));
        var ctx = Ctx(source);
        ctx.AcceptedFindings = [Fixable()];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.PostedFindings); // Mechanism B would re-post; the fix must not duplicate
        var (threadId, text) = Assert.Single(source.Replies);
        Assert.Equal(7, threadId);
        Assert.Contains("### 🔧 Quote the variable", text);
        Assert.Contains("```suggestion", text);
    }

    [Fact]
    public async Task Live_fix_reply_skips_exact_duplicate_on_retry()
    {
        var source = new FakePullRequestSource();
        var oldComment = new ThreadComment("b", "bot", true, "old finding", DateTimeOffset.UtcNow);
        source.Threads.Add(new ReviewThread(7, "k1", ReviewThreadStatus.Active, [oldComment]));
        var ctx = Ctx(source);
        ctx.AcceptedFindings = [Fixable()];
        var stage = new PublishFindingsStage(
            source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance);

        await stage.ExecuteAsync(ctx, CancellationToken.None);
        var reply = Assert.Single(source.Replies).Text;

        source.Threads[0] = source.Threads[0] with
        {
            Comments = [oldComment, new ThreadComment("b", "bot", true, reply, DateTimeOffset.UtcNow)],
        };
        source.Replies.Clear();

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.Replies);
    }

    [Fact]
    public async Task Commanded_fix_posts_suggestion_thread_without_dedupe_property_and_links_back()
    {
        var source = new FakePullRequestSource();
        var proposal = new FixProposal(
            "script.sh", 3, 3, "echo \"$name\"", "Quoted as requested.",
            FixOrigin.LlmCommanded, SourceThreadId: 42);
        var ctx = Ctx(source);
        ctx.AppliedFixes =
        [
            new AppliedFix("thread-42", proposal, "none"),
        ];
        ctx.FixCommands = [new FixCommand(42, new ThreadAnchor("script.sh", 3, 3), null, "please quote this")];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var (anchor, body, _) = Assert.Single(source.PostedSuggestions);
        Assert.Equal("script.sh", anchor.FilePath);
        Assert.Equal(3, anchor.StartLine);
        Assert.Null(Assert.Single(source.PostedSuggestionDedupeKeys)); // invisible to triage/publish
        Assert.Contains("### 🔧 Requested fix", body);
        Assert.Contains("**Requested via `/rf fix` on thread #42** — Quoted as requested.", body);
        Assert.Contains("> please quote this", body);
        Assert.Contains("AI-generated from the thread command — verify before accepting", body);
        Assert.Contains("```suggestion\necho \"$name\"\n```", body);
        var link = Assert.Single(source.Replies, r => r.ThreadId == 42);
        Assert.Contains("Fix posted above ⤴ (suggestion for script.sh:3–3).", link.Text);
    }

    [Fact]
    public async Task Commanded_fix_skips_preseeded_matching_suggestion_and_link()
    {
        var source = new FakePullRequestSource();
        var proposal = new FixProposal(
            "script.sh", 3, 3, "echo \"$name\"", "Quoted as requested.",
            FixOrigin.LlmCommanded, SourceThreadId: 42);
        var anchor = new ThreadAnchor("script.sh", 3, 3);
        var body = CommentFormatter.FormatFixedFinding(proposal, "please quote this");
        source.Threads.Add(new ReviewThread(
            900, null, ReviewThreadStatus.Active,
            [new ThreadComment("bot", "bot", true, body, DateTimeOffset.UtcNow)], anchor));
        var ctx = Ctx(source);
        ctx.AppliedFixes = [new AppliedFix("thread-42", proposal, "none")];
        ctx.FixCommands = [new FixCommand(42, anchor, null, "please quote this")];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.PostedSuggestions);
        Assert.DoesNotContain(source.Replies, reply => reply.ThreadId == 42);
    }

    [Fact]
    public async Task Commanded_fix_does_not_reply_when_claim_is_lost_after_suggestion()
    {
        var source = new FakePullRequestSource();
        var proposal = new FixProposal(
            "script.sh", 3, 3, "echo \"$name\"", "Quoted as requested.",
            FixOrigin.LlmCommanded, SourceThreadId: 42);
        var anchor = new ThreadAnchor("script.sh", 3, 3);
        var ctx = Ctx(source);
        ctx.AppliedFixes = [new AppliedFix("thread-42", proposal, "none")];
        ctx.FixCommands = [new FixCommand(42, anchor, null, "please quote this")];
        var guardCalls = 0;
        ctx.PublishGuard = () => Interlocked.Increment(ref guardCalls) <= 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(source.PostedSuggestions);
        Assert.Empty(source.Replies);
    }

    [Fact]
    public async Task Queued_command_replies_post_with_bot_preamble_and_dedupe_against_retry()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.FixCommandReplies = [(42, "I couldn't derive a safe fix for this one.")];

        var stage = new PublishFindingsStage(
            source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance);

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        var (_, text) = Assert.Single(source.Replies, r => r.ThreadId == 42);
        Assert.StartsWith(CommentFormatter.BotPreamble, text);
        Assert.Contains("couldn't derive a safe fix", text);

        // Retry with the same text: the re-fetched thread's last bot comment matches → skip.
        var replyText = text;
        source.Threads.Add(new ReviewThread(42, null, ReviewThreadStatus.Active,
            [new ThreadComment("bot", "bot", true, replyText, DateTimeOffset.UtcNow)]));
        source.Replies.Clear();

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain(source.Replies, r => r.ThreadId == 42);
    }

    [Fact]
    public async Task Publish_rechecks_head_before_sequential_auto_fix_writes()
    {
        var source = new HeadChangingPullRequestSource();
        source.Threads.Add(new ReviewThread(7, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "old finding", DateTimeOffset.UtcNow)]));
        var ctx = Ctx(source);
        ctx.AcceptedFindings = [Fixable()];

        await Assert.ThrowsAsync<PrHeadChangedException>(() =>
            new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Empty(source.Replies);
    }

    [Fact]
    public async Task Summary_counts_auto_fixes()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.AcceptedFindings = [Fixable()];
        ctx.AppliedFixes = [Fixable().AppliedFix!, new AppliedFix(
            "thread-42",
            new FixProposal("a.sh", 1, 1, "x", "r", FixOrigin.LlmCommanded, 42),
            "none")];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var summary = Assert.Single(source.GeneralComments);
        Assert.Contains("> **Auto-fixes:** 2 suggestion(s) posted — review and apply individually.", summary);
    }

    [Fact]
    public async Task Summary_excludes_regressed_fixed_finding_suppressed_by_triage()
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(7, "k1", ReviewThreadStatus.Fixed,
            [new ThreadComment("b", "bot", true, "old finding", DateTimeOffset.UtcNow)]));
        var finding = Fixable();
        finding.IsRegression = true;
        var ctx = Ctx(source);
        ctx.Collector.MarkRegressed("k1");
        ctx.AcceptedFindings = [finding];
        ctx.AppliedFixes = [finding.AppliedFix!];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var summary = Assert.Single(source.GeneralComments);
        Assert.DoesNotContain("Auto-fixes", summary);
    }

    [Fact]
    public async Task No_fixes_no_summary_line()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var summary = Assert.Single(source.GeneralComments);
        Assert.DoesNotContain("Auto-fixes", summary);
    }

    [Fact]
    public void FormatFixedFinding_renders_suggestion_block_with_verbatim_replacement()
    {
        var finding = Fixable();
        var text = CommentFormatter.FormatFinding(finding);

        Assert.StartsWith(CommentFormatter.BotPreamble, text);
        Assert.Contains("### 🔧 Quote the variable", text);
        Assert.Contains("```suggestion", text);
        Assert.Contains("echo \"$name\"", text);
        Assert.EndsWith("```\n", text);
    }

    [Fact]
    public void FormatFixedFinding_commanded_binds_excerpt_to_one_bounded_line()
    {
        var proposal = new FixProposal(
            "a.sh", 1, 1, "x", "rationale here", FixOrigin.LlmCommanded, 7);
        var excerpt = "line one\nline two " + new string('y', 300);
        var text = CommentFormatter.FormatFixedFinding(proposal, excerpt);

        Assert.Contains("### 🔧 Requested fix", text);
        Assert.Contains("#7", text);
        Assert.Contains("rationale here", text);
        Assert.Contains("> line one line two ", text);
        Assert.DoesNotContain("\nline two", text);
        Assert.Contains("AI-generated", text);
    }

    [Fact]
    public void FormatFixedFinding_uses_longer_fence_for_backticks_in_replacement()
    {
        const string replacement = "line ``` inside";
        var finding = Fixable();
        finding.AppliedFix = new AppliedFix(
            "k1",
            new FixProposal("script.sh", 3, 3, replacement, "r", FixOrigin.Deterministic),
            "none");

        var deterministic = CommentFormatter.FormatFinding(finding);
        Assert.Contains("````suggestion\nline ``` inside\n````", deterministic);

        var commanded = CommentFormatter.FormatFixedFinding(
            new FixProposal("script.sh", 3, 3, replacement, "r", FixOrigin.LlmCommanded, 42),
            "please quote this");
        Assert.Contains("````suggestion\nline ``` inside\n````", commanded);
    }

    private sealed class HeadChangingPullRequestSource : FakePullRequestSource
    {
        private int _fetches;

        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        {
            var fetch = Interlocked.Increment(ref _fetches);
            return Task.FromResult(fetch == 1 ? Pr : Pr with {SourceCommitSha = "changed-head"});
        }
    }
}
