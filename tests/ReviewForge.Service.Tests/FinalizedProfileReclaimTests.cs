using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class FinalizedProfileReclaimTests
{
    [Theory]
    [InlineData(false, 0, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 2, true, false)]
    [InlineData(true, 2, true, false)]
    [InlineData(false, 0, false, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(false, 3, false, false)]
    [InlineData(true, 3, false, false)]
    [InlineData(false, 0, true, true)]
    [InlineData(true, 0, true, true)]
    [InlineData(false, 0, false, true)]
    [InlineData(true, 0, false, true)]
    public async Task Crash_after_profile_finalization_before_ack_does_not_execute_again(bool sqlite, int detailState, bool success, bool competingClaim)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-reclaim-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var time = new FakeTimeProvider();
            var key = new PrKey("o", "p", "r", 1);
            var request = new ReviewRequest(Guid.NewGuid(), key, time.GetUtcNow());
            var originalQueue = new SqliteReviewQueue(connection, clock: time);
            Assert.True(originalQueue.TryEnqueue(request).Accepted);
            await using (var pending = originalQueue.ReadAllAsync(ct).GetAsyncEnumerator(ct))
            {
                Assert.True(await pending.MoveNextAsync());
                Assert.Equal(request.RunId, pending.Current.RunId);
            }
            IFindingStore store = sqlite ? new SqliteFindingStore(connection) : new FakeFindingStore();
            var profiles = (IModelProfileStore)store;
            var collector = new ModelProfileCollector(request.RunId);
            if (detailState != 1)
                collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5")
                    .Complete(detailState == 3 ? "interrupted" : "completed", null);
            var run = new ReviewRun(request.RunId, key, "pinned-head", ReviewKind.Full, time.GetUtcNow(), time.GetUtcNow(), success, []);
            await store.SaveRunAsync(run, ct);
            await profiles.FinalizeModelProfileAsync(collector.Snapshot(), time.GetUtcNow(),
                time.GetUtcNow().AddMinutes(detailState == 2 ? 1 : 60), false, ct);
            // Crash window: leave the durable queue claimed, but both terminal run and profile are committed.
            time.Advance(TimeSpan.FromMinutes(11));
            if (detailState == 2) await profiles.ExpireModelProfilesAsync(time.GetUtcNow(), 1, ct);
            if (sqlite) store = new SqliteFindingStore(connection);
            profiles = (IModelProfileStore)store;
            var before = JsonSerializer.Serialize(await profiles.ReadModelProfileAsync(request.RunId, 0, 100, time.GetUtcNow(), ct));
            var queue = new SqliteReviewQueue(connection, clock: time);
            var tracker = new RunTracker();
            var claims = new InFlightClaims(time);
            var holder = competingClaim ? Guid.NewGuid() : request.RunId;
            Assert.True(claims.TryClaim(key, holder, out _));
            var builder = new CountingBuilder();
            using var worker = new ReviewWorker(queue, Options.Create(new HostOptions { WorkerCount = 1 }),
                tracker, builder, claims, store, NullLogger<ReviewWorker>.Instance, time);
            await worker.StartAsync(ct);
            try
            {
                for (var i = 0; i < 200 && (await queue.GetPendingAsync([key], ct)).Count > 0; i++)
                    await Task.Delay(10, ct);
                Assert.Empty(await queue.GetPendingAsync([key], ct));
            }
            finally { await worker.StopAsync(ct); }
            Assert.Equal(0, builder.BuildCalls);
            Assert.Equal(success ? RunState.Completed : RunState.Failed, tracker.Get(request.RunId)!.State);
            var statusService = new RunSubmissionService(queue, tracker, claims, store, time,
                NullLogger<RunSubmissionService>.Instance);
            Assert.Equal(success ? RunState.Completed : RunState.Failed,
                (await statusService.GetStatusAsync(request.RunId, ct))!.State);
            Assert.False(claims.IsHeldBy(key, request.RunId));
            Assert.Equal(competingClaim, claims.IsHeldBy(key, holder));
            Assert.Equal(competingClaim ? 1 : 0, claims.ActiveCount);
            Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(await store.GetRunAsync(request.RunId, ct)));
            Assert.Equal(before, JsonSerializer.Serialize(await profiles.ReadModelProfileAsync(request.RunId, 0, 100, time.GetUtcNow(), ct)));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Theory]
    [InlineData("profile-read", 1, false)]
    [InlineData("run-read", 1, false)]
    [InlineData("shutdown", 1, false)]
    [InlineData("profile-read", 2, false)]
    [InlineData("run-read", 2, false)]
    [InlineData("shutdown", 2, false)]
    [InlineData("profile-read", 1, true)]
    [InlineData("run-read", 1, true)]
    [InlineData("shutdown", 1, true)]
    [InlineData("profile-read", 2, true)]
    [InlineData("run-read", 2, true)]
    [InlineData("shutdown", 2, true)]
    public async Task Failed_reclaim_preflight_preserves_terminal_records_and_leaves_request_for_reclaim(string failure, int workerCount, bool competingClaim)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-preflight-{Guid.NewGuid():N}.db");
        var ct = TestContext.Current.CancellationToken;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var time = new FakeTimeProvider();
            var key = new PrKey("o", "p", "r", 1);
            var request = new ReviewRequest(Guid.NewGuid(), key, time.GetUtcNow());
            var queue = new SqliteReviewQueue($"Data Source={path};Pooling=False", clock: time);
            Assert.True(queue.TryEnqueue(request).Accepted);
            var store = new FakeFindingStore();
            var run = new ReviewRun(request.RunId, key, "completed-head", ReviewKind.Full,
                time.GetUtcNow().AddMinutes(-5), time.GetUtcNow().AddMinutes(-1), true,
                [new("key", "rule", "high", "retained finding", "file.cs", 1, 12, Published: true)],
                LastObservedCommentAt: time.GetUtcNow().AddMinutes(-2));
            await store.SaveRunAsync(run, ct);
            var collector = new ModelProfileCollector(request.RunId);
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            await store.FinalizeModelProfileAsync(collector.Snapshot(), time.GetUtcNow(), time.GetUtcNow().AddDays(30), false, ct);
            var profileBefore = JsonSerializer.Serialize(await store.ReadModelProfileAsync(request.RunId, 0, 100, time.GetUtcNow(), ct));
            if (failure == "profile-read") store.ThrowOnModelProfileRead = true;
            if (failure == "run-read") store.BeforeGetRun = _ => throw new InvalidOperationException("terminal read failed");
            if (failure == "shutdown") store.BeforeModelProfileRead = _ => lifetime.Cancel();
            var claims = new InFlightClaims(time);
            var holder = competingClaim ? Guid.NewGuid() : request.RunId;
            Assert.True(claims.TryClaim(key, holder, out _));
            var tracker = new RunTracker();
            var builder = new CountingBuilder();
            using var worker = new ReviewWorker(queue, Options.Create(new HostOptions { WorkerCount = workerCount }),
                tracker, builder, claims, store, NullLogger<ReviewWorker>.Instance, time);
            await worker.StartAsync(lifetime.Token);
            try
            {
                if (failure == "shutdown")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), ct));
                else
                    await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), ct));
            }
            finally { await worker.StopAsync(ct); }
            store.ThrowOnModelProfileRead = false;
            store.BeforeGetRun = null;
            store.BeforeModelProfileRead = null;
            Assert.Equal(0, builder.BuildCalls);
            Assert.Single(await queue.GetPendingAsync([key], ct));
            Assert.Equal(RunState.Queued, tracker.Get(request.RunId)!.State);
            Assert.False(claims.IsHeldBy(key, request.RunId));
            Assert.Equal(competingClaim, claims.IsHeldBy(key, holder));
            Assert.Equal(competingClaim ? 1 : 0, claims.ActiveCount);
            Assert.Equal(JsonSerializer.Serialize(run), JsonSerializer.Serialize(await store.GetRunAsync(request.RunId, ct)));
            Assert.Equal(profileBefore, JsonSerializer.Serialize(await store.ReadModelProfileAsync(request.RunId, 0, 100, time.GetUtcNow(), ct)));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private sealed class CountingBuilder : IPipelineBuilder
    {
        public int BuildCalls { get; private set; }
        public ReviewPipeline Build(RunKind kind, ReviewRequest request, ReviewContext context, IRunProgress? progress = null)
        {
            BuildCalls++;
            context.ModelProfile.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5").Complete("completed", null);
            return new([], NullLogger<ReviewPipeline>.Instance);
        }
    }
}
