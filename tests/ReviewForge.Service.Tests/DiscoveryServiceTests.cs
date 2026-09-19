using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Queue;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DiscoveryServiceTests
{
    private static PullRequestCandidate Candidate(
        int prId,
        string branch = "main",
        string creatorId = "alice",
        string creatorName = "Alice",
        string headSha = "head",
        bool draft = false)
        => new(
            new PrKey("o", "p", "r", prId),
            new PullRequest(prId, "t", null, headSha, "base", "url", draft),
            branch, creatorId, creatorName);

    private static DiscoveryService Service(
        FakePullRequestSource source, FakeFindingStore store, ReviewQueue queue, RunTracker tracker,
        DiscoveryOptions? options = null, InFlightClaims? claims = null)
        => new(source, store, queue, tracker, claims ?? new InFlightClaims(), options ?? new DiscoveryOptions {TargetBranches = ["main"]});

    [Fact]
    public async Task Sweep_skips_draft_branch_and_creator_but_enqueues_survivor()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests =
            [
                Candidate(1, draft: true),
                Candidate(2, branch: "feature/x"),
                Candidate(3, creatorId: "mallory", creatorName: "Mallory"),
                Candidate(4),
            ],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var store = new FakeFindingStore();
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var options = new DiscoveryOptions {TargetBranches = ["main"], Creators = ["alice"]};
        var service = Service(source, store, queue, tracker, options);

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Equal(4, report.Candidates);
        Assert.Equal(1, report.Interesting);
        Assert.Equal(new PrKey("o", "p", "r", 4), Assert.Single(report.Enqueued));
        Assert.Equal(3, report.Skipped.Count);
        Assert.Contains(report.Skipped, s => s.Reason == "draft");
        Assert.Contains(report.Skipped, s => s.Reason == "target branch 'feature/x' not in filter");
        Assert.Contains(report.Skipped, s => s.Reason == "creator 'mallory' not in filter");
    }

    [Fact]
    public async Task Sweep_skips_pr_already_in_flight()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [Candidate(1), Candidate(2)],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var claims = new InFlightClaims();
        Assert.True(claims.TryClaim(new PrKey("o", "p", "r", 1), Guid.NewGuid(), out _));
        var service = Service(source, new FakeFindingStore(), new ReviewQueue(), new RunTracker(), claims: claims);

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Equal(new PrKey("o", "p", "r", 2), Assert.Single(report.Enqueued));
        Assert.Contains(report.Skipped, s => s.Reason == "review already in flight");
    }

    [Fact]
    public async Task Work_items_are_fetched_only_for_metadata_survivors()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [Candidate(1, draft: true), Candidate(2)],
            WorkItems = [],
        };
        var store = new FakeFindingStore();
        var service = Service(source, store, new ReviewQueue(), new RunTracker());

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, source.WorkItemFetches);
        Assert.Contains(report.Skipped, s => s.Reason == "draft");
        Assert.Contains(report.Skipped, s => s.Reason == "no linked work items");
        Assert.Empty(report.Enqueued);
    }

    [Fact]
    public async Task Already_reviewed_head_is_skipped_by_store_precheck()
    {
        var candidate = Candidate(1, headSha: "head-42");
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [candidate],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var store = new FakeFindingStore
        {
            LastRun = new PriorRun(candidate.Key, "head-42", DateTimeOffset.UtcNow, []),
        };
        var service = Service(source, store, new ReviewQueue(), new RunTracker());

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, store.LastRunFetches);
        Assert.Contains(report.Skipped, s => s.Reason == "head already reviewed");
        Assert.Empty(report.Enqueued);
    }

    [Fact]
    public async Task Enqueue_cap_reports_extra_candidates_as_skipped()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [Candidate(1), Candidate(2), Candidate(3)],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var store = new FakeFindingStore();
        var service = Service(source, store, new ReviewQueue(), new RunTracker(),
            new DiscoveryOptions {TargetBranches = ["main"], MaxEnqueuesPerSweep = 2});

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Equal(3, report.Interesting);
        Assert.Equal(2, report.Enqueued.Count);
        Assert.Contains(report.Skipped, s => s.Reason == "enqueue cap reached");
    }

    [Fact]
    public async Task Enqueued_run_is_tracked_as_queued()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [Candidate(1)],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var store = new FakeFindingStore();
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var service = Service(source, store, queue, tracker);

        var report = await service.RunSweepAsync(CancellationToken.None);

        Assert.Single(report.Enqueued);
        await foreach (var request in queue.ReadAllAsync(CancellationToken.None))
        {
            Assert.Equal(RunState.Queued, tracker.Get(request.RunId)!.State);
            break;
        }
    }
}

public class DiscoverySweepWorkerTests
{
    private static PullRequestCandidate Candidate(int prId)
        => new(
            new PrKey("o", "p", "r", prId),
            new PullRequest(prId, "t", null, "head", "base", "url", IsDraft: false),
            "main", "alice", "Alice");

    [Fact]
    public async Task Worker_is_disabled_when_no_interval()
    {
        var source = new FakePullRequestSource();
        var options = new DiscoveryOptions {TargetBranches = ["main"], SweepInterval = null};
        var service = new DiscoveryService(source, new FakeFindingStore(), new ReviewQueue(), new RunTracker(), new InFlightClaims(), options);
        var worker = new DiscoverySweepWorker(service, options, NullLogger<DiscoverySweepWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, source.OpenPullRequestsFetches);
    }

    [Fact]
    public async Task Worker_sweeps_on_tick_and_enqueues()
    {
        var source = new FakePullRequestSource
        {
            OpenPullRequests = [Candidate(1)],
            WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")],
        };
        var store = new FakeFindingStore();
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var options = new DiscoveryOptions {TargetBranches = ["main"], SweepInterval = TimeSpan.FromMilliseconds(50)};
        var service = new DiscoveryService(source, store, queue, tracker, new InFlightClaims(), options);
        var worker = new DiscoverySweepWorker(service, options, NullLogger<DiscoverySweepWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var workerTask = worker.StartAsync(cts.Token);

        await foreach (var request in queue.ReadAllAsync(CancellationToken.None))
        {
            Assert.Equal(RunState.Queued, tracker.Get(request.RunId)!.State);
            break;
        }

        Assert.True(source.OpenPullRequestsFetches > 0);
        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch (OperationCanceledException)
        {
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_logs_and_continues_when_a_sweep_fails()
    {
        var source = new ThrowingPullRequestSource();
        var options = new DiscoveryOptions {TargetBranches = ["main"], SweepInterval = TimeSpan.FromMilliseconds(25)};
        var worker = new DiscoverySweepWorker(
            new DiscoveryService(source, new FakeFindingStore(), new ReviewQueue(), new RunTracker(), new InFlightClaims(), options),
            options,
            NullLogger<DiscoverySweepWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.StartAsync(cts.Token);
        while (source.Calls == 0)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        Assert.True(source.Calls > 0);
    }

    private sealed class ThrowingPullRequestSource : FakePullRequestSource
    {
        public int Calls { get; private set; }

        public override Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("sweep failed");
        }
    }
}

[Collection("ReviewForge service host")]
public class DiscoveryEndpointTests : IAsyncLifetime
{
    private readonly ReviewForgeFactory _Factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Discover_enqueues_survivor_and_reports()
    {
        var candidate = new PullRequestCandidate(
            new PrKey("o", "p", "r", 99),
            new PullRequest(99, "t", null, "head-99", "base", "url", IsDraft: false),
            "main", "alice", "Alice");
        _Factory.Source.OpenPullRequests = [candidate];
        _Factory.Source.WorkItems = [new WorkItem(1, "t", "bug", null, null, "New")];

        var response = await _Factory.CreateClient().PostAsync("/reviews/discover", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<DiscoveryReport>();
        Assert.NotNull(report);
        Assert.Equal(1, report.Candidates);
        Assert.Equal(1, report.Interesting);
        Assert.Equal(candidate.Key, Assert.Single(report.Enqueued));
        Assert.Empty(report.Skipped);
    }
}