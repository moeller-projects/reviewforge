using ReviewForge.Core.Domain;
using ReviewForge.Service.Queue;
using Xunit;

namespace ReviewForge.Service.Tests;

public class QueueTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Fact]
    public async Task Enqueued_requests_are_read_in_order()
    {
        var queue = new ReviewQueue();
        var t0 = DateTimeOffset.UtcNow;
        await queue.EnqueueAsync(new ReviewRequest(Guid.NewGuid(), Key, t0), CancellationToken.None);
        await queue.EnqueueAsync(new ReviewRequest(Guid.NewGuid(), Key with {PrId = 2}, t0), CancellationToken.None);
        queue.Complete();

        var read = new List<ReviewRequest>();
        await foreach (var request in queue.ReadAllAsync(CancellationToken.None))
        {
            read.Add(request);
        }

        Assert.Equal(2, read.Count);
        Assert.Equal(1, read[0].Pr.PrId);
        Assert.Equal(2, read[1].Pr.PrId);
    }

    [Fact]
    public void Tracker_set_get_and_miss()
    {
        var tracker = new RunTracker();
        var id = Guid.NewGuid();

        Assert.Null(tracker.Get(id));

        tracker.Set(id, Key, RunState.Queued);
        tracker.Set(id, Key, RunState.Running);
        var status = tracker.Get(id);

        Assert.NotNull(status);
        Assert.Equal(RunState.Running, status.State);
        Assert.Equal(Key, status.Pr);
        Assert.Equal(id, status.RunId);

        tracker.Set(id, Key, RunState.Failed, "boom");
        Assert.Equal("boom", tracker.Get(id)!.Detail);
    }
}