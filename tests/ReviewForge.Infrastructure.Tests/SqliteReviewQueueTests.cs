using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class SqliteReviewQueueTests : IDisposable
{
    private readonly string _DbPath = Path.Combine(Path.GetTempPath(), "rf-queue-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var suffix in new[] {string.Empty, "-wal", "-shm"})
        {
            var path = _DbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static SqliteReviewQueue NewQueue(string dbPath, int capacity = 100, TimeProvider? clock = null)
        => new($"Data Source={dbPath};Pooling=False", capacity, clock);

    private static ReviewRequest Request(int prId, DateTimeOffset? enqueuedAt = null)
        => new(Guid.NewGuid(), new PrKey("o", "p", "r", prId), enqueuedAt ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task Dequeue_returns_enqueues_in_fifo_order_with_full_round_trip()
    {
        var queue = NewQueue(_DbPath);
        var t0 = DateTimeOffset.UtcNow;
        Assert.True(queue.TryEnqueue(new ReviewRequest(
            Guid.NewGuid(), new PrKey("o", "p", "r", 1), t0, HeadSha: "head-1")).Accepted);
        Assert.True(queue.TryEnqueue(Request(2, t0.AddSeconds(1))).Accepted);

        var first = await ClaimOneAsync(queue);
        var second = await ClaimOneAsync(queue);

        Assert.Equal(1, first.Pr.PrId);
        Assert.Equal("head-1", first.HeadSha);
        Assert.Equal(t0, first.EnqueuedAt);
        Assert.Equal(2, second.Pr.PrId);
    }

    [Fact]
    public void Enqueue_rejects_past_capacity_with_unclaimed_depth()
    {
        var queue = NewQueue(_DbPath, capacity: 2);
        Assert.True(queue.TryEnqueue(Request(1)).Accepted);
        Assert.True(queue.TryEnqueue(Request(2)).Accepted);

        var rejected = queue.TryEnqueue(Request(3));

        Assert.False(rejected.Accepted);
        Assert.Equal(2, rejected.QueueDepth);
        Assert.Equal(2, queue.ApproximateDepth);
    }

    [Fact]
    public async Task Racing_consumers_never_double_claim_a_row()
    {
        var queue = NewQueue(_DbPath);
        const int total = 40;
        for (var i = 1; i <= total; i++)
        {
            Assert.True(queue.TryEnqueue(Request(i)).Accepted);
        }

        using var cts = new CancellationTokenSource();
        var claimed = new ConcurrentBag<Guid>();
        var consumers = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await foreach (var request in queue.ReadAllAsync(cts.Token))
                {
                    claimed.Add(request.RunId);
                    queue.Acknowledge(request.RunId);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // drained; the test canceled the enumerator
            }
        }).ToArray();

        while (claimed.Count < total)
        {
            await Task.Delay(10).WaitAsync(TimeSpan.FromSeconds(30));
        }

        await cts.CancelAsync();
        await Task.WhenAll(consumers);

        Assert.Equal(total, claimed.Distinct().Count());
        Assert.Equal(0, queue.ApproximateDepth);
    }

    [Fact]
    public async Task Expired_claims_are_reclaimed_for_crash_recovery()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = NewQueue(_DbPath, clock: clock);
        var request = Request(1);
        Assert.True(queue.TryEnqueue(request).Accepted);
        Assert.NotNull(await ClaimOneAsync(queue)); // claimed by a "worker" that then dies

        clock.Advance(TimeSpan.FromMinutes(11)); // past the 10-minute claim TTL

        var reclaimed = await ClaimOneAsync(queue);
        Assert.Equal(request.RunId, reclaimed.RunId); // same row, re-claimed
    }

    [Fact]
    public async Task Trace_context_round_trips_through_the_row()
    {
        var queue = NewQueue(_DbPath);
        var context = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded,
            "rf=test");
        Assert.True(queue.TryEnqueue(new ReviewRequest(
            Guid.NewGuid(), new PrKey("o", "p", "r", 7), DateTimeOffset.UtcNow, context)).Accepted);

        var claimed = await ClaimOneAsync(queue);

        Assert.Equal(context.TraceId, claimed.EnqueueContext!.Value.TraceId);
        Assert.Equal(context.SpanId, claimed.EnqueueContext.Value.SpanId);
        Assert.Equal(context.TraceFlags, claimed.EnqueueContext.Value.TraceFlags);
        Assert.Equal("rf=test", claimed.EnqueueContext.Value.TraceState);
    }

    [Fact]
    public async Task Acknowledge_removes_the_row_and_queued_lookup_only_sees_unclaimed_rows()
    {
        var queue = NewQueue(_DbPath);
        var request = Request(1);
        Assert.True(queue.TryEnqueue(request).Accepted);
        Assert.Equal(request.RunId, queue.TryGetQueued(request.RunId)?.RunId);

        var claimed = await ClaimOneAsync(queue);
        Assert.Null(queue.TryGetQueued(request.RunId)); // claimed rows are not "queued"

        queue.Acknowledge(claimed.RunId);
        Assert.Equal(0, queue.ApproximateDepth);
        Assert.Null(queue.TryGetQueued(request.RunId));
    }

    /// <summary>Claims exactly one row, tolerating the poll interval when the queue is empty.</summary>
    private static async Task<ReviewRequest> ClaimOneAsync(SqliteReviewQueue queue)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var request in queue.ReadAllAsync(cts.Token))
        {
            return request;
        }

        throw new InvalidOperationException("enumeration ended without a claim");
    }
}
