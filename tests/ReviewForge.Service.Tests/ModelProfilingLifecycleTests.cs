using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ModelProfilingLifecycleTests
{
    [Fact]
    public void Defaults_and_all_typed_bounds_are_validated()
    {
        var options = new ModelProfilingOptions();
        Assert.Equal(30, options.RetentionDays);
        Assert.Equal(10_000, options.DetailLimit);
        Assert.True(Validator.TryValidateObject(options, new(options), [], true));
        foreach (var invalid in new[] { new ModelProfilingOptions { RetentionDays = 0 }, new() { RetentionDays = 3651 },
            new() { DetailLimit = 0 }, new() { DetailLimit = 10_001 }, new() { CleanupBatchSize = 0 },
            new() { CleanupBatchSize = 101 }, new() { CleanupIntervalMinutes = 0 }, new() { CleanupIntervalMinutes = 1441 } })
            Assert.False(Validator.TryValidateObject(invalid, new(invalid), [], true));
    }

    [Fact]
    public async Task Cleanup_is_bounded_periodic_and_preserves_summaries()
    {
        var time = new FakeTimeProvider();
        var store = new FakeFindingStore();
        var ct = TestContext.Current.CancellationToken;
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var collector = new ModelProfileCollector(Guid.NewGuid());
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            var snapshot = collector.Snapshot();
            ids.Add(snapshot.RunId);
            await store.FinalizeModelProfileAsync(snapshot, time.GetUtcNow().AddDays(-31), time.GetUtcNow(), false, ct);
        }
        using var worker = new ModelProfileCleanupService(store, Options.Create(new ModelProfilingOptions {
            CleanupBatchSize = 1, CleanupIntervalMinutes = 1 }), time);
        await worker.StartAsync(ct);
        // Await the startup batch without looking at private hosted-service implementation.
        for (var i = 0; i < 100 && (await store.ReadModelProfileAsync(ids[0], 0, 1, time.GetUtcNow().AddTicks(-1), ct)).Calls.Count > 0; i++)
            await Task.Delay(5, ct);
        Assert.Empty((await store.ReadModelProfileAsync(ids[0], 0, 1, time.GetUtcNow().AddTicks(-1), ct)).Calls);
        Assert.Single((await store.ReadModelProfileAsync(ids[1], 0, 1, time.GetUtcNow().AddTicks(-1), ct)).Calls);
        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 100 && (await store.ReadModelProfileAsync(ids[1], 0, 1, time.GetUtcNow().AddTicks(-TimeSpan.TicksPerMinute - 1), ct)).Calls.Count > 0; i++)
            await Task.Delay(5, ct);
        await worker.StopAsync(ct);
        var page = await store.ReadModelProfileAsync(ids[1], 0, 1, time.GetUtcNow(), ct);
        Assert.Empty(page.Calls);
        Assert.Equal(1, page.ObservedCalls);
    }

    [Fact]
    public async Task Cleanup_failures_are_observed_not_silently_swallowed()
    {
        using var service = new ModelProfileCleanupService(new FakeFindingStore { ThrowOnModelProfileCleanup = true },
            Options.Create(new ModelProfilingOptions()), TimeProvider.System);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteTask!);
    }

    [Theory]
    [InlineData(StageId.ReviewGate)]
    [InlineData(StageId.ResolveGate)]
    public async Task Gate_success_records_only_incomplete_marker_and_gate_skip_persists_nothing(StageId id)
    {
        var store = new FakeFindingStore();
        using var context = new ReviewContext(new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        await new ProgressStage(id, new GateStage(false), store: store).ExecuteAsync(context, TestContext.Current.CancellationToken);
        var profile = await store.ReadModelProfileAsync(context.RunId, 0, 10, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal("incomplete", profile.State);
        Assert.Empty(store.Runs);
        using var skipped = new ReviewContext(context.Pr, context.StartedAt);
        await new ProgressStage(id, new GateStage(true), store: store).ExecuteAsync(skipped, TestContext.Current.CancellationToken);
        Assert.Equal("legacy-unavailable", (await store.ReadModelProfileAsync(skipped.RunId, 0, 10, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).State);
        Assert.Empty(store.Runs);
        Assert.Null(ModelCallContext.Current);
    }

    private sealed class GateStage(bool skip) : IReviewStage
    {
        public string Name => "gate";
        public Task ExecuteAsync(ReviewContext context, CancellationToken ct)
        {
            if (skip) context.Terminate("skipped");
            return Task.CompletedTask;
        }
    }
}
