using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class DashboardInventoryCacheTests
{
    // Single-use source: a controllable in-flight inventory is specific to these cache tests.
    private sealed class Source : FakePullRequestSource
    {
        public int Calls;
        public CancellationToken FetchToken;
        public TaskCompletionSource<PullRequestInventory> Completion = new();
        public override Task<PullRequestInventory> GetOpenPullRequestInventoryAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            FetchToken = ct;
            return Completion.Task;
        }
    }

    [Fact]
    public async Task Shares_inflight_fetch_and_ttl_and_waiter_cancellation_is_independent()
    {
        var source = new Source();
        var clock = new FakeTimeProvider();
        var cache = new DashboardInventoryCache(source, clock);
        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetAsync(cts.Token);
        var waiting = cache.GetAsync(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(source.FetchToken.IsCancellationRequested);
        source.Completion.SetResult(new PullRequestInventory([], true, []));
        var first = await waiting;
        Assert.Same(first, await cache.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(30));
        source.Completion = new();
        var expired = cache.GetAsync(TestContext.Current.CancellationToken);
        var secondWaiter = cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(expired.IsCompletedSuccessfully, "Stale inventory must not wait for source refresh");
        Assert.Equal(first.FetchedAt, (await expired).FetchedAt);
        source.Completion.SetResult(new PullRequestInventory([], false, ["partial"]));
        Assert.True((await secondWaiter).Inventory.IsComplete);
        var second = await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(clock.GetUtcNow(), second.FetchedAt);
        Assert.False(second.Inventory.IsComplete);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Stale_refresh_failure_is_safe_shared_and_recovers_after_retry_window()
    {
        var source = new Source();
        var clock = new FakeTimeProvider();
        using var cache = new DashboardInventoryCache(source, clock);
        source.Completion.SetResult(new([], true, []));
        var first = await cache.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        source.Completion = new();
        source.Completion.SetException(new InvalidOperationException("secret path"));
        var failed = await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(failed.IsStale);
        Assert.True(failed.RefreshFailed);
        Assert.False(failed.Refreshing);
        Assert.Equal(first.FetchedAt, failed.FetchedAt);
        await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Calls);
        clock.Advance(TimeSpan.FromSeconds(30));
        source.Completion = new();
        source.Completion.SetResult(new([], false, ["partial"]));
        var recovered = await cache.GetAsync(TestContext.Current.CancellationToken);
        Assert.False(recovered.IsStale);
        Assert.False(recovered.RefreshFailed);
        Assert.False(recovered.Inventory.IsComplete);
        Assert.Equal(clock.GetUtcNow(), recovered.FetchedAt);
    }

    [Fact]
    public async Task Cache_owned_timeout_bounds_even_a_source_ignoring_cancellation_and_allows_retry()
    {
        var source = new Source();
        var clock = new FakeTimeProvider();
        using var cache = new DashboardInventoryCache(source, clock);
        var initial = cache.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(DashboardInventoryCache.FetchTimeout - TimeSpan.FromSeconds(1));
        Assert.False(initial.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial);
        Assert.True(source.FetchToken.IsCancellationRequested);
        source.Completion = new();
        source.Completion.SetResult(new([], true, []));
        Assert.True((await cache.GetAsync(TestContext.Current.CancellationToken)).Inventory.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_stop_or_disposal_cancels_shared_fetch(bool hostStop)
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder().Build();
        var lifetime = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(host.Services);
        var source = new Source();
        using var cache = new DashboardInventoryCache(source, new FakeTimeProvider(), lifetime);
        var waiting = cache.GetAsync(TestContext.Current.CancellationToken);
        if (hostStop) lifetime.StopApplication(); else cache.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(source.FetchToken.IsCancellationRequested);
        if (!hostStop) Assert.Throws<ObjectDisposedException>(() => { _ = cache.GetAsync(CancellationToken.None); });
    }

    [Fact]
    public async Task Stalled_refresh_returns_fresh_operational_merge_without_another_source_call()
    {
        var source = new Source();
        var clock = new FakeTimeProvider();
        using var cache = new DashboardInventoryCache(source, clock);
        var pr = new PrKey("org", "project", "repo", 1);
        source.Completion.SetResult(new([new(new(pr, source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []));
        var tracker = new RunTracker();
        var service = new DashboardQueryService(cache, tracker, new ReviewQueue(), new InFlightClaims(), new FakeFindingStore(), clock);
        var first = await service.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        source.Completion = new();
        var id = Guid.NewGuid();
        tracker.Register(new(id, pr, clock.GetUtcNow()));
        tracker.Set(id, pr, RunState.Failed);
        var read = service.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(read.IsCompletedSuccessfully);
        var refreshed = await read;
        Assert.True(refreshed.InventoryStale);
        Assert.True(refreshed.InventoryRefreshing);
        Assert.False(refreshed.InventoryRefreshFailed);
        Assert.Equal(first.InventoryFetchedAt, refreshed.InventoryFetchedAt);
        Assert.Equal(RunState.Failed, Assert.Single(refreshed.Rows).LatestPrAttempt!.State);
        await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Faults_are_not_cached_and_pre_cancelled_callers_do_not_fetch()
    {
        var source = new Source();
        var cache = new DashboardInventoryCache(source, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(cts.Token));
        Assert.Equal(0, source.Calls);
        source.Completion.SetException(new InvalidOperationException("failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(TestContext.Current.CancellationToken));
        source.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Completion.SetResult(new PullRequestInventory([], true, []));
        Assert.True((await cache.GetAsync(TestContext.Current.CancellationToken)).Inventory.IsComplete);
        Assert.Equal(2, source.Calls);
    }
}
