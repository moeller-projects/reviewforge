using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
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
        public ReviewWorker Worker { get; }

        public Harness()
        {
            _WorkDir = Path.Combine(Path.GetTempPath(), "reviewforge-worker-" + Guid.NewGuid().ToString("N"));
            Claims = new InFlightClaims(Clock, Ttl);
            var options = Options.Create(new ReviewForgeServiceOptions {WorkDir = _WorkDir});
            var factory = new ReviewPipelineFactory(
                Source,
                new FakeFindingStore(),
                new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), _WorkDir),
                new FakeChatClientFactory(new ScriptedChatClient(
                    ScriptedChatClient.FunctionCalls(
                        ("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "done"})))),
                options,
                LoggerFactory.Create(_ => { }));
            Worker = new ReviewWorker(Queue, Tracker, factory, Claims, NullLogger<ReviewWorker>.Instance, Clock);
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
}
