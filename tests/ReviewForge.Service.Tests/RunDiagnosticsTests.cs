using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunDiagnosticsTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("skipped")]
    public async Task Actual_execution_records_clock_duration_and_safe_outcome(string outcome)
    {
        var clock = new FakeTimeProvider();
        using var activity = new System.Diagnostics.Activity("diagnostic-test").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        using var ctx = new ReviewContext(Key, clock.GetUtcNow());
        var original = outcome == "cancelled" ? new OperationCanceledException("secret-provider-token")
            : new Exception("secret-provider-token");
        var stage = new ProgressStage(StageId.ReviewGate, new ActionStage(c =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            if (outcome is "failed" or "cancelled") throw original;
            if (outcome == "skipped") c.Terminate("untrusted termination text");
        }), clock: clock);
        if (outcome is "failed" or "cancelled")
            Assert.Same(original, await Record.ExceptionAsync(() => stage.ExecuteAsync(ctx, TestContext.Current.CancellationToken)));
        else await stage.ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        var snapshot = ctx.SnapshotDiagnostics();
        Assert.Equal(activity.TraceId.ToString(), snapshot.TraceId);
        var execution = Assert.Single(snapshot.Stages);
        Assert.Equal(outcome, execution.Outcome);
        Assert.Equal(ctx.StartedAt, execution.StartedAt);
        Assert.Equal(clock.GetUtcNow(), execution.CompletedAt);
        Assert.Equal(2000d, execution.DurationMilliseconds);
        Assert.DoesNotContain("secret-provider-token", JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("untrusted termination text", JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public async Task Journal_never_inserts_skipped_rows_and_retains_final_stage_completion()
    {
        var store = new FakeFindingStore();
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        await new ProgressStage(StageId.ReviewGate, new ActionStage(_ => { }), store: store).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Empty(store.Runs);
        await new ProgressStage(StageId.PersistRun, new SaveStage(store), store: store).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        var saved = Assert.Single(store.Runs);
        Assert.True(saved.Success);
        Assert.Equal("completed", saved.Diagnostics!.Stages.Last().Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_journal_failure_is_recorded_and_original_exception_is_preserved(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException("private credential") : new InvalidOperationException("private credential");
        var store = new FakeFindingStore { ThrowOnDiagnosticsUpdate = failure };
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        var stage = new ProgressStage(StageId.PersistRun, new SaveStage(store), store: store);
        Assert.Same(failure, await Record.ExceptionAsync(() => stage.ExecuteAsync(ctx, TestContext.Current.CancellationToken)));
        var diagnostic = Assert.Single(ctx.SnapshotDiagnostics().Stages);
        Assert.Equal(cancelled ? "cancelled" : "failed", diagnostic.Outcome);
        Assert.Equal(cancelled ? "journal-write-cancelled" : "journal-write-failed", diagnostic.FailureCode);
        Assert.DoesNotContain("private credential", JsonSerializer.Serialize(diagnostic));
    }

    [Fact]
    public async Task Existing_shell_gets_running_snapshot_without_changing_its_outcome()
    {
        var store = new FakeFindingStore();
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        ctx.PlannedStages = ["BeginRun", "ReviewGate", "PersistRun"];
        await new ProgressStage(StageId.BeginRun, new SaveStage(store), store: store).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        await new ProgressStage(StageId.ReviewGate, new ActionStage(_ => {
            var run = Assert.Single(store.Runs);
            Assert.True(run.Success);
            Assert.Equal(3, run.Diagnostics!.PlannedStages.Count);
            Assert.Equal(2, run.Diagnostics.Stages.Count);
            Assert.Equal("running", run.Diagnostics.Stages.Last().Outcome);
            Assert.Null(run.Diagnostics.Stages.Last().CompletedAt);
        }), store: store).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal("completed", store.Runs[0].Diagnostics!.Stages.Last().Outcome);
    }

    [Fact]
    public void Summary_is_bounded_and_model_provenance_is_not_invented()
    {
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        ctx.Reasoning = new() { Result = new ReviewResult {
            Narrative = new() { ReviewSummary = new string('x', 10000) }, Findings = [], Uncertainties = [] } };
        var snapshot = ctx.SnapshotDiagnostics();
        Assert.EndsWith("[truncated]", snapshot.Context.ReviewSummary);
        Assert.True(snapshot.Context.ReviewSummary!.Length <= 2012);
        Assert.Equal("unavailable", snapshot.Context.ModelProvenance);
    }

    [Fact]
    public void Context_uses_available_result_metadata_and_bounds_each_explanatory_collection()
    {
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        ctx.Repository = new() { ReviewableFiles = ["a.cs", "b.cs"] };
        ctx.Reasoning = new() { Result = new ReviewResult {
            RuleBookVersion = "rules-v1", ReviewDepth = "tool-loop",
            Narrative = new() { AcceptanceCriteria = Enumerable.Range(0, 30)
                .Select(i => new AcVerdict(i, "criterion", AcStatus.Unclear, "evidence")).ToArray() },
            Findings = [], Uncertainties = Enumerable.Range(0, 30)
                .Select(i => new ReviewUncertainty("topic", "question", "context")).ToArray() } };
        var context = ctx.SnapshotDiagnostics().Context;
        Assert.Equal("rules-v1", context.RuleBookVersion);
        Assert.Equal("tool-loop", context.ReviewDepth);
        Assert.Equal(2, context.ReviewableFileCount);
        Assert.Equal(20, context.AcceptanceCriteria.Count);
        Assert.Equal(20, context.Uncertainties.Count);
        Assert.True(context.CollectionsTruncated);
    }

    [Fact]
    public void Summary_redacts_credential_shapes_and_never_projects_thread_actions()
    {
        using var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        ctx.Reasoning = new() { Result = new ReviewResult {
            Narrative = new() {
                ReviewSummary = "Authorization: Bearer private-bearer password=private-password https://user:private-password@host/secret",
                ThreadActions = [new(1, ThreadActionKind.Answer, "private-tool-transcript")] },
            Findings = [], Uncertainties = [] } };
        var json = JsonSerializer.Serialize(ctx.SnapshotDiagnostics());
        Assert.DoesNotContain("private-bearer", json);
        Assert.DoesNotContain("private-password", json);
        Assert.DoesNotContain("private-tool-transcript", json);
        Assert.Contains("[redacted]", json);
    }

    private sealed class ActionStage(Action<ReviewContext> action) : IReviewStage
    {
        public string Name => "test";
        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct) { action(ctx); return Task.CompletedTask; }
    }
    private sealed class SaveStage(FakeFindingStore store) : IReviewStage
    {
        public string Name => "persist-run";
        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct) => store.SaveRunAsync(new ReviewRun(
            ctx.RunId, ctx.Pr, "head", ReviewKind.Full, ctx.StartedAt, DateTimeOffset.UtcNow, true, []), ct);
    }
}
