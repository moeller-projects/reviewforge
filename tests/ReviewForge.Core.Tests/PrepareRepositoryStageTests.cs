using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class PrepareRepositoryStageTests : IDisposable
{
    private static readonly PrKey Key = new("o", "p", "r", 1);
    private readonly string _Root = Path.Combine(Path.GetTempPath(), "reviewforge-prepare-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_Root))
        {
            Directory.Delete(_Root, recursive: true);
        }
    }

    private static PullRequest PullRequest() => new(
        1, "title", null, "head-sha", "base-sha", "https://clone", false,
        "creator-1", "author", "refs/heads/feature/test");

    private static AutoFixOptions CommitMode() => new()
    {
        Enabled = true,
        PublishMode = AutoFixOptions.ModeCommitOnHead,
        CommitAuthorName = "reviewforge[bot]",
        CommitAuthorEmail = "reviewforge@example.com",
    };

    private static PrepareRepositoryStage Stage(
        RepoCheckoutPool pool, CheckoutMode mode = CheckoutMode.Pooled, AutoFixOptions? options = null)
        => new(pool, NullLogger<PrepareRepositoryStage>.Instance, checkoutMode: mode, autoFix: options);

    [Fact]
    public async Task Private_mode_uses_run_checkout_and_disposal_removes_it()
    {
        var pool = new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), _Root);
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome {PullRequest = PullRequest()},
        };

        await Stage(pool, CheckoutMode.Private).ExecuteAsync(ctx, CancellationToken.None);
        var privatePath = pool.PrivatePath(ctx.RunId);

        Assert.Equal(privatePath, ctx.Repository.RepoDir);
        Assert.True(Directory.Exists(privatePath));
        Assert.NotNull(ctx.Repository.Diff);
        ctx.Dispose();
        Assert.False(Directory.Exists(privatePath));
    }

    [Fact]
    public async Task Pooled_mode_remains_shared_and_populates_head_info()
    {
        var headInfo = new TipCommitInfo(
            "reviewforge@example.com",
            "fix(src): repair\n\nReviewForge-Run: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n");
        var pool = new RepoCheckoutPool(new FakeGitOps {HeadInfo = headInfo}, new FakeWorkspaceFs(), _Root);
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome {PullRequest = PullRequest()},
        };

        await Stage(pool).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(pool.CheckoutPath("r", "head-sha"), ctx.Repository.RepoDir);
        Assert.Equal(headInfo, ctx.Repository.HeadCommitInfo);
        ctx.Dispose();
        Assert.True(Directory.Exists(pool.CheckoutPath("r", "head-sha")));
    }

    [Fact]
    public async Task Uses_merge_base_to_build_diff_and_match_provider_scope()
    {
        const string diff = "diff --git a/src/file.cs b/src/file.cs\n--- a/src/file.cs\n+++ b/src/file.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var git = new FakeGitOps {Diff = diff, MergeBaseSha = "pr-merge-base"};
        var pool = new RepoCheckoutPool(git, new FakeWorkspaceFs(), _Root);
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome
            {
                PullRequest = PullRequest(),
                ChangedFileManifest = [new ChangedFile("src/file.cs", ChangedFileType.Edit)],
            },
        };

        await Stage(pool).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal([("base-sha", "head-sha")], git.MergeBaseRequests);
        Assert.Equal([("pr-merge-base", "head-sha")], git.DiffRequests);
        Assert.Equal(new[] {"src/file.cs"}, ctx.Repository.Diff!.Files);
        ctx.Dispose();
    }

    [Fact]
    public async Task Loop_guard_terminates_only_discovery_runs_on_matching_bot_head()
    {
        var headInfo = new TipCommitInfo(
            "reviewforge@example.com",
            "fix(src): repair\n\nReviewForge-Run: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\n");
        var git = new FakeGitOps {HeadInfo = headInfo};
        var pool = new RepoCheckoutPool(git, new FakeWorkspaceFs(), _Root);
        var options = CommitMode();
        var discovery = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome {PullRequest = PullRequest()},
            Trigger = EnqueueTrigger.Discovery,
        };
        var manual = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome {PullRequest = PullRequest()},
            Trigger = EnqueueTrigger.Manual,
        };
        var unknownHead = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome {PullRequest = PullRequest()},
            Trigger = EnqueueTrigger.Discovery,
        };
        await Stage(pool, options: options).ExecuteAsync(discovery, CancellationToken.None);
        Assert.True(discovery.Terminated);
        Assert.Equal("bot-authored head", discovery.TerminationReason);
        Assert.Equal(headInfo, discovery.Repository.HeadCommitInfo);
        discovery.Dispose(); // release the per-head lease before the next run acquires it

        await Stage(pool, options: options).ExecuteAsync(manual, CancellationToken.None);
        Assert.False(manual.Terminated);
        Assert.NotNull(manual.Repository.Diff);
        manual.Dispose();

        git.HeadInfo = null;
        await Stage(pool, options: options).ExecuteAsync(unknownHead, CancellationToken.None);
        Assert.False(unknownHead.Terminated);
        Assert.Null(unknownHead.Repository.HeadCommitInfo);
        Assert.NotNull(unknownHead.Repository.Diff);
        unknownHead.Dispose();
    }
}