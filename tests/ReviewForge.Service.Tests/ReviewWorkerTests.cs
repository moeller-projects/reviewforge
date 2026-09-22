using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service.Queue;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ReviewWorkerTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

    private sealed class Harness : IDisposable
    {
        private readonly string _WorkDir;
        public FakeTimeProvider Clock { get; } = new();
        public ReviewQueue Queue { get; } = new();
        public RunTracker Tracker { get; } = new();
        public InFlightClaims Claims { get; }
        public FakePullRequestSource Source { get; } = new();
        public FakeFindingStore Store { get; }
        public ReviewWorker Worker { get; }

        public ReviewPipelineFactory Factory { get; }

        public Harness(
            FakePullRequestSource? source = null,
            FakeGitOps? git = null,
            FakeFindingStore? store = null,
            string cleanVote = "Approved")
        {
            _WorkDir = Path.Combine(Path.GetTempPath(), "reviewforge-worker-" + Guid.NewGuid().ToString("N"));
            // The real host creates these at startup (P3-m); this direct-factory harness
            // must prepare its own fixtures.
            Directory.CreateDirectory(Path.Combine(_WorkDir, "findings"));
            Claims = new InFlightClaims(Clock, Ttl);
            Store = store ?? new FakeFindingStore();
            Source = source ?? new FakePullRequestSource();
            var options = Options.Create(new ReviewForgeServiceOptions {WorkDir = _WorkDir, CleanRunVote = cleanVote});
            Factory = new ReviewPipelineFactory(
                Source,
                Store,
                new RepoCheckoutPool(git ?? new FakeGitOps(), new FakeWorkspaceFs(), _WorkDir),
                new FakeChatClientFactory(new ScriptedChatClient(
                    ScriptedChatClient.FunctionCalls(
                        ("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})))),
                options,
                LoggerFactory.Create(_ => { }));
            Worker = new ReviewWorker(Queue, Tracker, Factory, Claims, Store, NullLogger<ReviewWorker>.Instance, Clock);
        }

        public void Dispose()
        {
            if (Directory.Exists(_WorkDir))
            {
                try
                {
                    Directory.Delete(_WorkDir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task Worker_skips_run_whose_claim_was_lost_while_queued()
    {
        using var h = new Harness();
        var runA = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runA, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runA, Key, h.Clock.GetUtcNow())).Accepted);

        h.Clock.Advance(TimeSpan.FromHours(3)); // A's claim expires while queued
        var runB = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runB, out _)); // a duplicate re-claims the PR

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runA)?.State != RunState.Skipped; i++)
        {
            await Task.Delay(25);
        }

        var status = h.Tracker.Get(runA);
        Assert.Equal(RunState.Skipped, status?.State);
        Assert.Equal("claim lost while queued", status?.Detail);
        Assert.Equal(0, h.Source.ThreadFetches); // pipeline never ran
        Assert.True(h.Claims.IsHeldBy(Key, runB)); // the winner's claim is untouched

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Worker_reclaims_expired_uncontested_claim_at_dequeue()
    {
        using var h = new Harness();
        var runA = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runA, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runA, Key, h.Clock.GetUtcNow())).Accepted);

        h.Clock.Advance(TimeSpan.FromHours(3)); // expired, but no competitor

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runA)?.State != RunState.Completed; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(RunState.Completed, h.Tracker.Get(runA)?.State);
        Assert.True(h.Source.ThreadFetches > 0); // pipeline ran
        Assert.True(h.Claims.TryClaim(Key, Guid.NewGuid(), out _)); // claim was released on completion

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Worker_runs_normally_when_claim_still_held()
    {
        using var h = new Harness();
        var runA = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runA, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runA, Key, h.Clock.GetUtcNow())).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runA)?.State != RunState.Completed; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(RunState.Completed, h.Tracker.Get(runA)?.State);

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Worker_persists_failed_run()
    {
        using var h = new Harness(git: new ThrowingGitOps());
        var runId = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runId, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runId, Key, h.Clock.GetUtcNow())).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runId)?.State != RunState.Failed; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(RunState.Failed, h.Tracker.Get(runId)?.State);
        var failed = Assert.Single(h.Store.RecentRuns);
        Assert.False(failed.Success);
        Assert.Equal(runId, failed.Id);
        Assert.Equal(Key, failed.Pr);

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Worker_preserves_enqueued_head_when_initial_fetch_fails()
    {
        using var h = new Harness(source: new FailingFetchSource());
        var runId = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runId, out _));
        Assert.True(h.Queue.TryEnqueue(
            new ReviewRequest(runId, Key, h.Clock.GetUtcNow(), HeadSha: "candidate-head")).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runId)?.State != RunState.Failed; i++)
        {
            await Task.Delay(25);
        }

        var failed = Assert.Single(h.Store.RecentRuns);
        Assert.Equal("candidate-head", failed.HeadSha);

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Worker_failure_persist_never_throws_when_store_fails()
    {
        using var h = new Harness(git: new ThrowingGitOps(), store: new ThrowingStore());
        var pr2 = Key with {PrId = 2};
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runA, out _));
        Assert.True(h.Claims.TryClaim(pr2, runB, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runA, Key, h.Clock.GetUtcNow())).Accepted);
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runB, pr2, h.Clock.GetUtcNow())).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runB)?.State != RunState.Failed; i++)
        {
            await Task.Delay(25);
        }

        // The throwing store did not kill the worker: both runs failed and were drained.
        Assert.Equal(RunState.Failed, h.Tracker.Get(runA)?.State);
        Assert.Equal(RunState.Failed, h.Tracker.Get(runB)?.State);

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ThrowingGitOps : FakeGitOps
    {
        public override Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
            => throw new InvalidOperationException("git exploded");
    }

    private sealed class ThrowingStore : FakeFindingStore
    {
        public override Task SaveRunAsync(ReviewRun run, CancellationToken ct)
            => throw new InvalidOperationException("store down");
    }

    private sealed class FailingFetchSource : FakePullRequestSource
    {
        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
            => Task.FromException<PullRequest>(new InvalidOperationException("fetch failed"));
    }

    /// <summary>Source that hangs in the PR fetch until the run token is cancelled.</summary>
    private sealed class StallingSource : FakePullRequestSource
    {
        public override async Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task Worker_returns_cleanly_when_host_cancels_mid_run()
    {
        using var h = new Harness(source: new StallingSource());
        var runId = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runId, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runId, Key, h.Clock.GetUtcNow())).Accepted);

        using var cts = new CancellationTokenSource();
        _ = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runId)?.State != RunState.Running; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(RunState.Running, h.Tracker.Get(runId)?.State); // stalled inside the run
        // StopAsync cancels the worker token and waits for the run loop to unwind — the
        // worker must swallow the shutdown OCE rather than rethrowing it.
        await h.Worker.StopAsync(CancellationToken.None);

        Assert.False(h.Claims.IsHeldBy(Key, runId), "claim must be released even on shutdown mid-run");
    }

    [Fact]
    public void Factory_accepts_clean_run_vote_none()
    {
        using var h = new Harness(cleanVote: "None");
        Assert.NotNull(h.Factory.Create());
    }

    [Fact]
    public void FactoryCreate_does_not_touch_the_filesystem()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-nofs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new ReviewForgeServiceOptions {WorkDir = root});
            var factory = new ReviewPipelineFactory(
                new FakePullRequestSource(),
                new FakeFindingStore(),
                new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), root),
                new FakeChatClientFactory(
                    new ScriptedChatClient(
                        ScriptedChatClient.FunctionCalls(
                            ("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})))),
                options,
                LoggerFactory.Create(_ => { }));

            factory.Create();
            factory.Create();

            // Only the host startup (P3-m) may create the runtime directories; the per-run
            // factory must not. (The pool itself creates checkouts/ + mirror/ on its own.)
            Assert.False(Directory.Exists(Path.Combine(root, "findings")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Returns a changed PR head on the second fetch (force-push mid-run).</summary>
    private sealed class HeadChangingSource : FakePullRequestSource
    {
        private int _Fetches;

        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        {
            var pull = _Fetches++ == 0
                ? new PullRequest(1, "t", null, "head-a", "base", "url", false)
                : new PullRequest(1, "t", null, "head-b", "base", "url", false);
            return Task.FromResult(pull);
        }
    }

    [Fact]
    public async Task Worker_marks_superseded_run_failed_without_error_log()
    {
        using var h = new Harness(source: new HeadChangingSource());
        var runId = Guid.NewGuid();
        Assert.True(h.Claims.TryClaim(Key, runId, out _));
        Assert.True(h.Queue.TryEnqueue(new ReviewRequest(runId, Key, h.Clock.GetUtcNow())).Accepted);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = h.Worker.StartAsync(cts.Token);

        for (var i = 0; i < 200 && h.Tracker.Get(runId)?.State != RunState.Failed; i++)
        {
            await Task.Delay(25);
        }

        var status = h.Tracker.Get(runId);
        Assert.Equal(RunState.Failed, status?.State);
        Assert.Contains("head changed during review", status?.Detail);
        Assert.False(h.Claims.IsHeldBy(Key, runId), "claim must be released after a superseded run");
        Assert.Empty(h.Source.PostedFindings); // nothing was published

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
