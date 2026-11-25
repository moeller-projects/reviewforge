using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardPresentationTests
{
    [Theory]
    [InlineData(RunState.Running, "Current")]
    [InlineData(RunState.Failed, "Failed")]
    [InlineData(RunState.Skipped, "Completed")]
    [InlineData(RunState.Completed, "Completed")]
    [InlineData(RunState.Queued, "Not started")]
    public void Stage_marks_use_loaded_order_and_outcome(RunState state, string mark)
    {
        var run = new DashboardRun(Guid.NewGuid(), "head", null, state, CurrentStage: "ReviewGate")
            { LoadedStages = ["FetchPrContext", "ReviewGate", "PersistRun"] };
        Assert.Equal(mark, StageLabels.Mark(run, "ReviewGate"));
        Assert.Equal(state == RunState.Queued ? "Not started" : "Completed", StageLabels.Mark(run, "FetchPrContext"));
        Assert.Equal(state == RunState.Completed ? "Completed" : "Not started", StageLabels.Mark(run, "PersistRun"));
        Assert.Equal(state == RunState.Completed ? "Completed" : state == RunState.Queued ? "Not started" : "Unknown", StageLabels.Mark(run with { CurrentStage = "future" }, "ReviewGate"));
    }

    [Fact]
    public void Unknown_and_pending_metadata_remain_honest_and_relative_times_are_readable()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("Time unknown", DashboardPresentation.Relative(null, now));
        Assert.Equal("Just now", DashboardPresentation.Relative(now, now));
        Assert.Equal("2m ago", DashboardPresentation.Relative(now.AddMinutes(-2), now));
        Assert.Equal("2h ago", DashboardPresentation.Relative(now.AddHours(-2), now));
        Assert.Equal("2d ago", DashboardPresentation.Relative(now.AddDays(-2), now));
        var run = new DashboardRun(Guid.NewGuid(), null, null, null);
        Assert.Equal("Stage unknown", DashboardPresentation.Stage(run));
        Assert.Equal("Outcome unknown", DashboardPresentation.Outcome(run));
        Assert.Equal("Claimed — state unknown", DashboardPresentation.Outcome(run with { IsClaimed = true }));
        Assert.Equal("No result yet.", DashboardPresentation.Detail(run));
        Assert.Equal("Waiting for worker", DashboardPresentation.Stage(run with { IsPending = true }));
        Assert.Equal("Unknown", StageLabels.Mark(run with { LoadedStages = ["FetchPrContext"], CurrentStage = "FetchPrContext" }, "FetchPrContext"));
    }

    [Fact]
    public void Friendly_labels_and_safe_links_and_indeterminate_progress()
    {
        Assert.Equal("Fetch PR context", StageLabels.Label("FetchPrContext"));
        Assert.Equal("Future step", StageLabels.Label("FutureStep"));
        Assert.Equal("Unknown stage", StageLabels.Label(null));
        foreach (var id in Enum.GetNames<StageId>()) Assert.NotEmpty(StageLabels.Label(id));
        Assert.Null(DashboardPresentation.ExternalUrl("javascript:alert(1)"));
        Assert.Null(DashboardPresentation.ExternalUrl("https://user:pass@example.com"));
        Assert.Null(DashboardPresentation.ExternalUrl("/relative"));
        Assert.Equal("https://example.com/", DashboardPresentation.ExternalUrl("https://example.com"));
        var run = new DashboardRun(Guid.NewGuid(), null, null, RunState.Running, ProgressPercent: 0);
        Assert.Null(DashboardPresentation.Progress(run));
        Assert.Equal("Waiting for worker", DashboardPresentation.Stage(run));
        Assert.Equal(100, DashboardPresentation.Progress(run with { State = RunState.Completed }));
        Assert.Equal("Coverage unknown", DashboardPresentation.Coverage(null));
        Assert.Equal("Unreviewed", DashboardPresentation.Coverage(false));
        Assert.Equal("Reviewed", DashboardPresentation.Coverage(true));
    }
}
