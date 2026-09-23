using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Queue;
using Xunit;

namespace ReviewForge.Service.Tests;

public class QueueTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Fact]
    public void TryEnqueue_rejects_when_full()
    {
        var queue = new ReviewQueue(capacity: 2);
        var first = queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow));
        var second = queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), Key with {PrId = 2}, DateTimeOffset.UtcNow));
        var third = queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), Key with {PrId = 3}, DateTimeOffset.UtcNow));

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.False(third.Accepted);
        Assert.Equal(2, third.QueueDepth);
    }

    [Fact]
    public async Task Enqueued_requests_are_read_in_order()
    {
        var queue = new ReviewQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), Key, t0));
        queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), Key with {PrId = 2}, t0));
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

    [Fact]
    public void Tracker_evicts_expired_statuses()
    {
        var clock = new FakeTimeProvider();
        var tracker = new RunTracker(clock, TimeSpan.FromHours(1));
        var id = Guid.NewGuid();
        tracker.Set(id, Key, RunState.Completed);

        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)));

        Assert.Null(tracker.Get(id));
    }

    [Fact]
    public void Tracker_keeps_only_newest_max_entries()
    {
        var clock = new FakeTimeProvider();
        var tracker = new RunTracker(clock, maxEntries: 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        tracker.Set(first, Key, RunState.Completed);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Set(second, Key, RunState.Completed);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.Set(third, Key, RunState.Completed);

        Assert.Null(tracker.Get(first));
        Assert.NotNull(tracker.Get(second));
        Assert.NotNull(tracker.Get(third));
    }

    [Fact]
    public void Tracker_keeps_queued_and_running_entries_over_terminal_cap()
    {
        var tracker = new RunTracker(maxEntries: 1);
        var queued = Guid.NewGuid();
        var running = Guid.NewGuid();
        tracker.Set(queued, Key, RunState.Queued);
        tracker.Set(running, Key, RunState.Running);

        Assert.NotNull(tracker.Get(queued));
        Assert.NotNull(tracker.Get(running));
    }

    [Fact]
    public void Tracker_does_not_overwrite_terminal_state_with_stale_queued()
    {
        var tracker = new RunTracker();
        var id = Guid.NewGuid();
        tracker.Set(id, Key, RunState.Completed);

        // A late Queued write from the enqueuing thread racing a fast worker must not resurrect it.
        tracker.Set(id, Key, RunState.Queued);
        tracker.Set(id, Key, RunState.Running);

        Assert.Equal(RunState.Completed, tracker.Get(id)!.State);
    }

    [Fact]
    public void Tracker_allows_terminal_refinement_last_terminal_wins()
    {
        var tracker = new RunTracker();
        var id = Guid.NewGuid();
        tracker.Set(id, Key, RunState.Completed);

        // Terminal → terminal transitions are allowed: a later failure supersedes a
        // premature completion (e.g. a persist failure surfaced post-hoc).
        tracker.Set(id, Key, RunState.Failed, "late failure");

        Assert.Equal(RunState.Failed, tracker.Get(id)!.State);
        Assert.Equal("late failure", tracker.Get(id)!.Detail);
    }

    [Fact]
    public void Tracker_remove_deletes_entry_and_missing_is_noop()
    {
        var tracker = new RunTracker();
        var id = Guid.NewGuid();
        tracker.Set(id, Key, RunState.Queued);

        tracker.Remove(id);

        Assert.Null(tracker.Get(id));
        tracker.Remove(id); // idempotent
    }
}