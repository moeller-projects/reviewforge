using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class CommitFixesStageTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    private static AutoFixOptions Options(string granularity = AutoFixOptions.GranularityPerFix)
        => new()
        {
            Enabled = true,
            AllowedAuthors = ["creator-1"],
            PublishMode = AutoFixOptions.ModeCommitOnHead,
            CommitGranularity = granularity,
            CommitAuthorName = "reviewforge[bot]",
            CommitAuthorEmail = "reviewforge@example.com",
        };

    private static RichFinding Finding(string key, string category = "bug", string severity = "medium")
        => new()
        {
            RuleId = "r", Title = "title-" + key, Severity = severity, Category = category, Description = "d",
            DedupeKey = key,
        };

    private static AppliedFix Fix(string key, string path, bool appliedToTree = true, int? threadId = null)
        => new(
            key,
            new FixProposal(path, 1, 2, "replacement", "rationale",
                threadId is null ? FixOrigin.Deterministic : FixOrigin.LlmCommanded, threadId))
        {
            AppliedToTree = appliedToTree,
        };

    private static ReviewContext Ctx(
        PullRequest? pr = null, params AppliedFix[] fixes)
        => new(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new()
            {
                PullRequest = pr ?? new PullRequest(
                    1, "t", null, "head-sha", "base", "https://clone", false, "creator-1", "PR Author",
                    SourceRefName: "refs/heads/feature/x")
            },
            Repository = new() {RepoDir = Path.GetTempPath()},
            AutoFix = new() {AppliedFixes = fixes},
            Validation = new()
            {
                AcceptedFindings = fixes
                    .Where(f => f.Proposal.SourceThreadId is null)
                    .Select(f => Finding(f.DedupeKey))
                    .ToArray(),
            },
        };

    private static CommitFixesStage Stage(FakeGitOps git, FakeFindingStore store, AutoFixOptions options)
        => new(git, store, options, NullLogger<CommitFixesStage>.Instance);

    [Fact]
    public void Stage_exposes_expected_name()
    {
        var stage = Stage(new FakeGitOps(), new FakeFindingStore(), Options());

        Assert.Equal("commit-fixes", stage.Name);
    }

    [Fact]
    public async Task Noop_when_commit_mode_is_not_active()
    {
        var git = new FakeGitOps();
        // Enabled=false and Suggestion mode both keep the stage inert.
        foreach (var inactive in new[]
                 {
                     new AutoFixOptions {Enabled = false, PublishMode = AutoFixOptions.ModeCommitOnHead},
                     new AutoFixOptions {Enabled = true, PublishMode = AutoFixOptions.ModeSuggestion},
                 })
        {
            var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));
            await Stage(git, new FakeFindingStore(), inactive).ExecuteAsync(ctx, CancellationToken.None);
        }

        Assert.Empty(git.Commits);
        Assert.Empty(git.Pushes);
    }

    [Fact]
    public async Task Noop_when_no_fixes_are_pending_commit()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh", appliedToTree: false));

        await Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(git.Commits);
        Assert.Empty(git.Pushes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("refs/pull/123/merge")]
    [InlineData("refs/heads/")]
    public async Task Bad_source_ref_degrades_before_any_commit(string? sourceRefName)
    {
        var git = new FakeGitOps();
        var pr = new PullRequest(
            1, "t", null, "head-sha", "base", "https://clone", false, "creator-1", "PR Author",
            SourceRefName: sourceRefName);
        var fix = Fix("k1", "src/a.sh");
        var ctx = Ctx(pr, fix);

        await Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(git.Commits);
        Assert.Empty(git.Pushes);
        Assert.Null(fix.CommitSha); // publishes as a suggestion instead
    }

    [Fact]
    public async Task Lost_claim_before_commit_fails_the_run()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));
        ctx.PublishGuard = () => false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Empty(git.Commits);
    }

    [Fact]
    public async Task Per_file_groups_commit_path_scoped_and_coalesce_same_file_fixes()
    {
        var git = new FakeGitOps();
        var store = new FakeFindingStore();
        var a1 = Fix("k1", "src/a.sh");
        var a2 = Fix("k2", "src/a.sh", threadId: null);
        var b1 = Fix("k3", "src/b.sh");
        var ctx = Ctx(null, a1, a2, b1);

        await Stage(git, store, Options()).ExecuteAsync(ctx, CancellationToken.None);

        // PerFix = one commit per FILE: two groups, deterministic path order.
        Assert.Equal(2, git.Commits.Count);
        Assert.Equal(["src/a.sh"], git.Commits[0].Paths);
        Assert.Equal(["src/b.sh"], git.Commits[1].Paths);
        Assert.Single(git.Pushes);
        Assert.Equal(("feature/x", "head-sha"), git.Pushes[0]);
        Assert.NotNull(a1.CommitSha);
        Assert.Equal(a1.CommitSha, a2.CommitSha); // same file ⇒ same commit
        Assert.NotEqual(a1.CommitSha, b1.CommitSha);
        Assert.All(new[] {a1.CommitSubject, a2.CommitSubject, b1.CommitSubject}, s => Assert.NotNull(s));
        // Pushed-fix rows are durable immediately after push, confirmed for reconciliation.
        Assert.Equal(3, store.PushedFixes.Count);
        Assert.All(store.PushedFixes, row => Assert.True(row.Pushed));
        Assert.All(store.PushedFixes, row => Assert.False(row.ReplyPosted));
    }

    [Fact]
    public async Task Single_granularity_makes_one_commit_for_the_run()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(null, Fix("k1", "src/a.sh"), Fix("k2", "src/b.sh"));

        await Stage(git, new FakeFindingStore(), Options(AutoFixOptions.GranularitySingle))
            .ExecuteAsync(ctx, CancellationToken.None);

        var commit = Assert.Single(git.Commits);
        Assert.Equal(["src/a.sh", "src/b.sh"], commit.Paths);
        Assert.Single(git.Pushes);
    }

    [Fact]
    public async Task Commit_messages_carry_the_run_trailer_and_builder_subject()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));

        await Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None);

        var message = Assert.Single(git.Commits).Message;
        Assert.True(LoopGuard.TryParseRunTrailer(message, out var trailerRunId));
        Assert.Equal(ctx.RunId, trailerRunId);
        Assert.StartsWith("fix(src): ", message);
    }

    [Fact]
    public async Task Nothing_to_commit_degrades_the_group_and_continues()
    {
        var git = new FakeGitOps {ThrowOnCommit = new InvalidOperationException("nothing to commit")};
        var store = new FakeFindingStore();
        var fix = Fix("k1", "src/a.sh");
        var ctx = Ctx(null, fix);

        await Stage(git, store, Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(fix.CommitSha);
        Assert.Empty(git.Pushes);
        Assert.Empty(store.PushedFixes);
    }

    [Fact]
    public async Task Lost_claim_before_push_fails_after_commits_without_pushing()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));
        var calls = 0;
        ctx.PublishGuard = () => ++calls < 2; // holds "before commit", lost "before push"

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(git.Commits);
        Assert.Empty(git.Pushes);
    }

    [Fact]
    public async Task Remote_tip_mismatch_at_the_pin_fails_with_head_changed()
    {
        var git = new FakeGitOps {RemoteTip = "someone-elses-sha"};
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));

        await Assert.ThrowsAsync<PrHeadChangedException>(() => Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Empty(git.Pushes);
    }

    [Fact]
    public async Task Lost_claim_during_tip_read_fails_without_pushing()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));
        var calls = 0;
        ctx.PublishGuard = () => ++calls < 3; // holds "before commit"/"before push", lost "at push"

        await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(git.Commits);
        Assert.Empty(git.Pushes);
    }

    [Fact]
    public async Task Missing_remote_branch_at_the_pin_fails_with_head_changed()
    {
        var git = new FakeGitOps {RemoteTip = null};
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));

        await Assert.ThrowsAsync<PrHeadChangedException>(() => Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Push_rejection_fails_the_run_and_persists_nothing()
    {
        var git = new FakeGitOps {ThrowOnPush = new InvalidOperationException("policy: TF402455")};
        var store = new FakeFindingStore();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Stage(git, store, Options()).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("TF402455", ex.Message);
        Assert.Empty(store.PushedFixes);
    }

    [Fact]
    public async Task Commanded_fixes_queue_fixed_in_replies_and_store_thread_ids()
    {
        var git = new FakeGitOps();
        var store = new FakeFindingStore();
        var fix = Fix("thread-42", "src/a.sh", threadId: 42);
        var ctx = Ctx(null, fix);

        await Stage(git, store, Options()).ExecuteAsync(ctx, CancellationToken.None);

        var (threadId, text) = Assert.Single(ctx.AutoFix.FixCommandReplies);
        Assert.Equal(42, threadId);
        Assert.StartsWith("Fixed in ", text);
        Assert.Contains(fix.CommitSha![..7], text);
        Assert.Contains(fix.CommitSubject!, text);
        Assert.Contains("AI-generated", text);
        var row = Assert.Single(store.PushedFixes);
        Assert.Equal(42, row.ThreadId);
        Assert.Equal("thread-42", row.DedupeKey);
        Assert.Equal(fix.CommitSha, row.CommitSha);
        Assert.True(row.AiDrafted);
    }

    [Fact]
    public async Task Pushed_head_is_stamped_for_the_publish_head_check()
    {
        var git = new FakeGitOps();
        var ctx = Ctx(fixes: Fix("k1", "src/a.sh"));

        await Stage(git, new FakeFindingStore(), Options()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(ctx.AutoFix.PushedHeadSha);
        Assert.Equal(ctx.AutoFix.AppliedFixes[0].CommitSha, ctx.AutoFix.PushedHeadSha);
    }
}