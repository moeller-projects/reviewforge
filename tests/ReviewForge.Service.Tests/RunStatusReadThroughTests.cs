using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunStatusReadThroughTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Theory]
    [InlineData(true, true, RunState.Completed, 100)]
    [InlineData(false, true, RunState.Failed, 0)]
    [InlineData(false, false, RunState.Running, 0)]
    public async Task Store_read_through_retains_known_metadata_without_inventing_history(
        bool success, bool completed, RunState state, int percent)
    {
        var store = new FakeFindingStore();
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full,
            DateTimeOffset.UtcNow.AddMinutes(-1), completed ? DateTimeOffset.UtcNow : null,
            success, [], Pipeline: nameof(RunKind.ReviewDraft));
        store.Runs.Add(run);
        var service = Create(new ReviewQueue(), new RunTracker(), store);
        var status = (await service.GetStatusAsync(run.Id, CancellationToken.None))!;
        Assert.Equal(state, status.State);
        Assert.Equal(percent, status.ProgressPercent);
        Assert.Equal(RunKind.ReviewDraft, status.Kind);
        Assert.Equal("head", status.HeadSha);
        Assert.Equal(run.StartedAt, status.StartedAt);
        Assert.Equal(run.CompletedAt, status.CompletedAt);
        Assert.Null(status.EnqueuedAt);
        Assert.Null(status.Trigger);
        Assert.Null(status.CurrentStage);
        Assert.Empty(status.LoadedStages);
    }

    [Fact]
    public async Task Queue_read_through_preserves_trigger_and_head_but_manual_submission_does_not_guess_head()
    {
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var service = Create(queue, tracker, new FakeFindingStore());
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow,
            HeadSha: "queued-head", Trigger: EnqueueTrigger.Discovery, Kind: RunKind.Resolve);
        Assert.True(queue.TryEnqueue(request).Accepted);
        var status = (await service.GetStatusAsync(request.RunId, CancellationToken.None))!;
        Assert.Equal(request.HeadSha, status.HeadSha);
        Assert.Equal(request.EnqueuedAt, status.EnqueuedAt);
        Assert.Equal(request.Trigger, status.Trigger);
        Assert.Equal(request.Kind, status.Kind);
        Assert.Null(status.StartedAt);
        Assert.Equal(0, status.ProgressPercent);

        var accepted = Assert.IsType<SubmitOutcome.Accepted>(service.Submit(
            new SubmitReviewRequest("o", "p", "r", 2), RunKind.Review));
        var tracked = tracker.Get(accepted.RunId)!;
        var queued = queue.TryGetQueued(accepted.RunId)!;
        Assert.Null(tracked.HeadSha);
        Assert.Equal(queued.EnqueuedAt, tracked.EnqueuedAt);
        Assert.Equal(EnqueueTrigger.Manual, tracked.Trigger);
    }

    private static RunSubmissionService Create(ReviewQueue queue, RunTracker tracker, FakeFindingStore store)
        => new(queue, tracker, new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
}
