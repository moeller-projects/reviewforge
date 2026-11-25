using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunProgressTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Fact]
    public void Positional_constructor_and_serialization_remain_compatible()
    {
        var status = new RunStatus(Guid.NewGuid(), Key, RunState.Running, null, DateTimeOffset.UtcNow);
        Assert.Equal(RunKind.Review, status.Kind);
        Assert.Empty(status.LoadedStages);
        Assert.Equal(0, status.ProgressPercent);
        Assert.Equal(100, (status with {State = RunState.Completed}).ProgressPercent);
        Assert.Contains("\"ProgressPercent\":0", JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData(false, false, 12)]
    [InlineData(true, false, 13)]
    [InlineData(false, true, 13)]
    [InlineData(true, true, 14)]
    public void Progress_uses_actual_definition_and_every_stage(bool verify, bool git, int count)
    {
        var definition = Pipelines.Review(new ReviewOptions(), new AutoFixOptions(),
            new VerifyFindingsOptions {Enabled = verify}, git);
        Assert.Equal(count, definition.Stages.Count);
        var tracker = new RunTracker();
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow);
        tracker.Register(request);
        Assert.Equal(0, tracker.Get(request.RunId)!.ProgressPercent);
        tracker.DefinitionLoaded(request.RunId, definition.Stages);
        Assert.Equal(RunState.Running, tracker.Get(request.RunId)!.State);
        Assert.Null(tracker.Get(request.RunId)!.CurrentStage);
        for (var i = 0; i < count; i++)
        {
            tracker.StageStarting(request.RunId, definition.Stages[i]);
            Assert.Equal(i * 100 / count, tracker.Get(request.RunId)!.ProgressPercent);
        }
        tracker.Set(request.RunId, Key, RunState.Failed);
        Assert.Equal((count - 1) * 100 / count, tracker.Get(request.RunId)!.ProgressPercent);
        tracker.Set(request.RunId, Key, RunState.Completed);
        Assert.Equal(100, tracker.Get(request.RunId)!.ProgressPercent);
    }

    [Fact]
    public void Metadata_is_preserved_and_stale_notifications_are_ignored()
    {
        var clock = new FakeTimeProvider();
        var tracker = new RunTracker(clock);
        var request = new ReviewRequest(Guid.NewGuid(), Key, clock.GetUtcNow(), HeadSha: "queued-head", Trigger: EnqueueTrigger.Discovery);
        tracker.Register(request);
        clock.Advance(TimeSpan.FromMinutes(1));
        StageId[] stages = [StageId.FetchPrContext, StageId.ReviewGate, StageId.PersistRun];
        tracker.DefinitionLoaded(request.RunId, stages);
        stages[0] = StageId.ApplyFixes;
        tracker.StageStarting(request.RunId, StageId.ReviewGate);
        tracker.CaptureHead(request.RunId, "fetched-head");
        var running = tracker.Get(request.RunId)!;
        tracker.StageStarting(request.RunId, StageId.FetchPrContext);
        tracker.StageStarting(request.RunId, StageId.ApplyFixes);
        tracker.Register(request);
        tracker.DefinitionLoaded(request.RunId, [StageId.PersistRun]);
        Assert.Equal(running, tracker.Get(request.RunId));
        clock.Advance(TimeSpan.FromMinutes(1));
        tracker.Set(request.RunId, Key, RunState.Skipped);
        var terminal = tracker.Get(request.RunId)!;
        Assert.Equal(66, terminal.ProgressPercent);
        Assert.Equal("fetched-head", terminal.HeadSha);
        Assert.Equal(request.EnqueuedAt, terminal.EnqueuedAt);
        Assert.Equal(running.StartedAt, terminal.StartedAt);
        Assert.Equal(clock.GetUtcNow(), terminal.CompletedAt);
        Assert.Equal(EnqueueTrigger.Discovery, terminal.Trigger);
        Assert.Equal("FetchPrContext", terminal.LoadedStages[0]);
        tracker.Set(request.RunId, Key, RunState.Running);
        tracker.Register(request);
        tracker.DefinitionLoaded(request.RunId, stages);
        tracker.StageStarting(request.RunId, StageId.PersistRun);
        tracker.CaptureHead(request.RunId, "late-head");
        Assert.Equal(terminal, tracker.Get(request.RunId));
        tracker.Set(request.RunId, Key, RunState.Completed);
        tracker.Set(request.RunId, Key, RunState.Failed, "correction");
        Assert.Equal("correction", tracker.Get(request.RunId)!.Detail);
        Assert.Equal(33, tracker.Get(request.RunId)!.ProgressPercent);
        Assert.Equal(terminal.CompletedAt, tracker.Get(request.RunId)!.CompletedAt);
    }

    [Theory]
    [InlineData(RunState.Queued)]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Skipped)]
    public void Unknown_or_empty_stages_never_guess_progress(RunState state)
    {
        var status = new RunStatus(Guid.NewGuid(), Key, state, null, DateTimeOffset.UtcNow);
        Assert.Equal(0, status.ProgressPercent);
        Assert.Equal(0, (status with {LoadedStages = ["FetchPrContext"], CurrentStage = "unknown"}).ProgressPercent);
        Assert.Equal(0, (status with {LoadedStages = ["FetchPrContext"], CurrentStage = "FetchPrContext", State = RunState.Queued}).ProgressPercent);
    }

    [Fact]
    public async Task Decorator_reports_before_execution_and_keeps_faulting_identity_and_telemetry_name()
    {
        var tracker = new RunTracker();
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow);
        tracker.Register(request);
        tracker.DefinitionLoaded(request.RunId, [StageId.ReviewGate]);
        var progress = new TrackedRunProgress(tracker, request.RunId);
        var stage = new ProgressStage(StageId.ReviewGate, new InspectingStage(() =>
        {
            Assert.Equal("ReviewGate", tracker.Get(request.RunId)!.CurrentStage);
            throw new InvalidOperationException("fault");
        }), progress);
        Assert.Equal("review-gate", stage.Name);
        using var context = new ReviewContext(Key, DateTimeOffset.UtcNow, request.RunId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(context, CancellationToken.None));
        tracker.Set(request.RunId, Key, RunState.Failed);
        Assert.Equal("ReviewGate", tracker.Get(request.RunId)!.CurrentStage);
        Assert.Equal(0, tracker.Get(request.RunId)!.ProgressPercent);
    }

    // Single-use stage for observing decorator ordering, not a shared adapter double.
    private sealed class InspectingStage(Action execute) : IReviewStage
    {
        public string Name => "review-gate";
        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
        {
            execute();
            return Task.CompletedTask;
        }
    }
}
