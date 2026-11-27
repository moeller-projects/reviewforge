using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class RunWorklistQueryServiceTests
{
    [Fact]
    public void History_port_uses_effective_store_registration()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        Assert.Same(factory.Store, factory.Services.GetRequiredService<IRunHistoryStore>());
        Assert.NotNull(factory.Services.GetRequiredService<RunWorklistQueryService>());
    }

    [Fact]
    public async Task Closed_pr_and_manual_unknown_head_survive_inventory_and_tracker_corrects_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakePullRequestSource { Inventory = new([], true, []) };
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var store = new FakeFindingStore();
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var pr = new PrKey("closed", "p", "r", 42);
        var old = new ReviewRun(Guid.NewGuid(), pr, "old", ReviewKind.Full, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, true, []);
        await store.SaveRunAsync(old, ct);
        var request = new ReviewRequest(Guid.NewGuid(), pr with { RepositoryId = "manual" }, DateTimeOffset.UtcNow);
        Assert.True(queue.TryEnqueue(request).Accepted);
        var service = new RunWorklistQueryService(cache, tracker, queue, new InFlightClaims(), store, TimeProvider.System);
        var first = await service.GetAsync(0, 100, null, false, ct);
        Assert.Equal(2, first.Items.Count);
        Assert.All(first.Items, item => Assert.Null(item.Title));
        Assert.Null(first.Items.Single(i => i.Run.RunId == request.RunId).Run.HeadSha);
        Assert.Empty(first.Items.Single(i => i.Run.RunId == old.Id).Run.LoadedStages);
        tracker.Register(request);
        tracker.Set(request.RunId, request.Pr, RunState.Skipped, "unsafe private data");
        Assert.Empty((await service.GetAsync(0, 100, null, true, ct)).Items);
        var corrected = (await service.GetAsync(0, 100, request.Pr, false, ct)).Items.Single();
        Assert.Equal(RunState.Skipped, corrected.Run.State);
        Assert.False(corrected.Run.IsPending);
        Assert.DoesNotContain("unsafe", corrected.Run.OutcomeDetail ?? "");
        store.ThrowOnHistory = new InvalidOperationException("store failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(0, 10, null, false, ct));
    }

    [Fact]
    public async Task Durable_completion_wins_over_tracker_still_running_in_all_and_attention_views()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new DashboardInventoryCache(new FakePullRequestSource(), TimeProvider.System);
        var store = new FakeFindingStore();
        var tracker = new RunTracker();
        var request = new ReviewRequest(Guid.NewGuid(), new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        tracker.Register(request);
        tracker.Set(request.RunId, request.Pr, RunState.Running);
        await store.SaveRunAsync(new(request.RunId, request.Pr, "head", ReviewKind.Full,
            request.EnqueuedAt, request.EnqueuedAt.AddMinutes(1), true, []), ct);
        var service = new RunWorklistQueryService(cache, tracker, new ReviewQueue(), new InFlightClaims(), store, TimeProvider.System);

        var completed = Assert.Single((await service.GetAsync(0, 100, null, false, ct)).Items);

        Assert.Equal(RunState.Completed, completed.Run.State);
        Assert.NotNull(completed.Run.CompletedAt);
        Assert.Empty((await service.GetAsync(0, 100, null, true, ct)).Items);
    }

    [Fact]
    public async Task Unmatched_mutation_claims_are_blockers_not_fictitious_run_identities()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new DashboardInventoryCache(new FakePullRequestSource(), TimeProvider.System);
        var store = new FakeFindingStore();
        var claims = new InFlightClaims();
        var queue = new ReviewQueue();
        var pr = new PrKey("o", "p", "r", 1);
        var mutationId = Guid.NewGuid();
        Assert.True(claims.TryClaim(pr, mutationId, out _));
        var service = new RunWorklistQueryService(cache, new RunTracker(), queue, claims, store, TimeProvider.System);
        Assert.Empty((await service.GetAsync(0, 100, null, false, ct)).Items);
        claims.Release(pr, mutationId);
        var request = new ReviewRequest(Guid.NewGuid(), pr, DateTimeOffset.UtcNow);
        Assert.True(queue.TryEnqueue(request).Accepted);
        Assert.True(claims.TryClaim(pr, request.RunId, out _));
        var queued = Assert.Single((await service.GetAsync(0, 100, null, false, ct)).Items);
        Assert.Equal(request.RunId, queued.Run.RunId);
        Assert.True(queued.Run.IsClaimed);
    }

    [Fact]
    public async Task Cold_source_failure_is_explicit_metadata_warning_not_loss_of_durable_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakePullRequestSource { ThrowOnGetInventory = new InvalidOperationException("private source failure") };
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var store = new FakeFindingStore();
        store.Runs.Add(new(Guid.NewGuid(), new("o", "p", "closed", 1), "", ReviewKind.Full, DateTimeOffset.UtcNow, null, false, []));
        var response = await new RunWorklistQueryService(cache, new RunTracker(), new ReviewQueue(), new InFlightClaims(), store, TimeProvider.System)
            .GetAsync(0, 10, null, false, ct);
        Assert.Single(response.Items);
        Assert.False(response.InventoryComplete);
        Assert.True(response.InventoryStale);
        Assert.NotEmpty(response.SourceWarnings);
        Assert.DoesNotContain(response.SourceWarnings, s => s.Contains("private"));
    }

    [Fact]
    public async Task Source_metadata_enriches_only_full_pr_identity_and_current_head_is_separate()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new FakePullRequestSource();
        var pr = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(pr, source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var store = new FakeFindingStore();
        var at = DateTimeOffset.UtcNow;
        var older = new ReviewRun(Guid.NewGuid(), pr, "old-head", ReviewKind.Full, at.AddHours(-2), at.AddHours(-1), true, []);
        store.Runs.Add(older);
        store.Runs.Add(older with { Id = Guid.NewGuid(), HeadSha = "new-head", StartedAt = at });
        store.Runs.Add(older with { Id = Guid.NewGuid(), Pr = pr with { Project = "other" } });
        var service = new RunWorklistQueryService(cache, new RunTracker(), new ReviewQueue(), new InFlightClaims(), store, TimeProvider.System);
        var response = await service.GetAsync(0, 100, null, false, ct);
        Assert.Equal(3, response.Items.Count);
        Assert.All(response.Items.Where(i => i.Pr == pr), i =>
        {
            Assert.Equal("Repo", i.RepositoryName);
            Assert.Equal(source.Pr.SourceCommitSha, i.CurrentHeadSha);
        });
        Assert.Null(response.Items.Single(i => i.Pr.Project == "other").Title);
        var group = RunWorklistGrouping.Group(response.Items, false).Single(g => g.Pr == pr);
        Assert.Equal(new[] { "new-head", "old-head" }, group.Revisions.Select(r => r.HeadSha));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_pending_snapshot_is_bounded_without_inventory_keys(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"pending-history-{Guid.NewGuid():N}.db");
        try
        {
            IReviewQueue queue = sqlite ? new ReviewForge.Infrastructure.Persistence.SqliteReviewQueue($"Data Source={path};Pooling=False") : new ReviewQueue();
            var first = new ReviewRequest(Guid.NewGuid(), new("o", "p", "r", 1), DateTimeOffset.UtcNow.AddMinutes(-1));
            var last = new ReviewRequest(Guid.NewGuid(), new("o", "p", "closed", 1), DateTimeOffset.UtcNow);
            Assert.True(queue.TryEnqueue(first).Accepted);
            Assert.True(queue.TryEnqueue(last).Accepted);
            Assert.Equal(last.RunId, Assert.Single(await queue.GetPendingHistoryAsync(1, ct)).Request.RunId);
            Assert.Equal(2, (await queue.GetPendingHistoryAsync(1000, ct)).Count);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queue.GetPendingHistoryAsync(0, ct));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_history_keeps_sub_millisecond_order_across_offsets(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"pending-ticks-{Guid.NewGuid():N}.db");
        try
        {
            IReviewQueue queue = sqlite ? new ReviewForge.Infrastructure.Persistence.SqliteReviewQueue($"Data Source={path};Pooling=False") : new ReviewQueue();
            var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var older = new ReviewRequest(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), new("o", "p", "r", 1), at.AddTicks(1));
            var newer = older with { RunId = Guid.Parse("00000000-0000-0000-0000-000000000001"), EnqueuedAt = at.AddTicks(2).ToOffset(TimeSpan.FromHours(2)) };
            Assert.True(queue.TryEnqueue(older).Accepted);
            Assert.True(queue.TryEnqueue(newer).Accepted);
            Assert.Equal(newer.RunId, Assert.Single(await queue.GetPendingHistoryAsync(1, ct)).Request.RunId);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    [Fact]
    public async Task Paging_merges_active_runs_before_history_without_duplicates_and_keeps_old_attention()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cache = new DashboardInventoryCache(new FakePullRequestSource { Inventory = new([], false, ["unavailable"]) }, TimeProvider.System);
        var store = new FakeFindingStore();
        var pr = new PrKey("o", "p", "r", 1);
        var at = DateTimeOffset.UtcNow.AddDays(-10);
        for (var i = 0; i < 105; i++) store.Runs.Add(new(Guid.NewGuid(), pr, i % 2 == 0 ? "zz" : "aa", ReviewKind.Full, at.AddMinutes(i), at.AddMinutes(i + 1), i != 0, []));
        var tracker = new RunTracker();
        var request = new ReviewRequest(Guid.NewGuid(), pr, DateTimeOffset.UtcNow);
        tracker.Register(request);
        var service = new RunWorklistQueryService(cache, tracker, new ReviewQueue(), new InFlightClaims(), store, TimeProvider.System);
        var first = await service.GetAsync(0, 1, null, false, ct);
        Assert.Equal(request.RunId, first.Items.Single().Run.RunId);
        Assert.True(first.HasMore);
        var next = await service.GetAsync(1, 100, null, false, ct);
        Assert.Equal(100, next.Items.Count);
        Assert.DoesNotContain(next.Items, i => i.Run.RunId == request.RunId);
        Assert.Equal(2, (await service.GetAsync(0, 100, null, true, ct)).Items.Count);
        Assert.Contains("unavailable", next.SourceWarnings);
    }
}
