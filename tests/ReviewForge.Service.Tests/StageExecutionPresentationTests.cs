using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class StageExecutionPresentationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static StageExecutionDiagnostic Stage(double? ms = 100, int seconds = 1, string outcome = "completed") =>
        new("ExecuteReasoning", Start.AddSeconds(seconds), Start.AddSeconds(seconds + 1), ms, outcome, null);

    [Fact]
    public void Measured_offsets_preserve_gaps_and_queue_wait_is_separate()
    {
        var rows = StageExecutionPresentation.Project([Stage(), Stage(200, 5)], Start, false);
        Assert.Equal(1000d, rows[0].OffsetMilliseconds);
        Assert.Equal(5000d, rows[1].OffsetMilliseconds);
        Assert.Equal(200d, rows[1].DurationMilliseconds);
        Assert.Equal(3000d, StageExecutionPresentation.QueueWait(Start.AddSeconds(-3), Start));
        Assert.Null(StageExecutionPresentation.QueueWait(Start, Start.AddSeconds(-1)));
        Assert.Null(StageExecutionPresentation.QueueWait(null, Start));
        Assert.Null(StageExecutionPresentation.QueueWait(Start, null));
        Assert.Equal(0d, StageExecutionPresentation.QueueWait(Start, Start));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_invalid_duration_is_unavailable_not_zero(double? ms)
    {
        var row = Assert.Single(StageExecutionPresentation.Project([Stage(ms)], Start, false));
        Assert.Null(row.DurationMilliseconds);
        Assert.False(row.HasBar);
        Assert.Equal("unavailable", StageExecutionPresentation.Milliseconds(row.DurationMilliseconds));
    }

    [Fact]
    public void Invalid_timestamps_never_create_negative_or_invented_bars()
    {
        var stages = new[] { Stage() with { StartedAt = default }, Stage(seconds: -1),
            Stage() with { CompletedAt = Start }, Stage() };
        var rows = StageExecutionPresentation.Project(stages, Start, false);
        Assert.Null(rows[0].OffsetMilliseconds);
        Assert.Null(rows[1].OffsetMilliseconds);
        Assert.False(rows[2].HasBar);
        Assert.Equal("Completed · partial", rows[0].Outcome);
        Assert.Equal("Completed · partial", rows[2].Outcome);
        Assert.True(rows[3].HasBar);
        Assert.All(StageExecutionPresentation.Project(stages, null, false), row => Assert.False(row.HasBar));
        Assert.All(StageExecutionPresentation.Project(stages, default(DateTimeOffset), false), row => Assert.False(row.HasBar));
        Assert.Equal("0 ms", StageExecutionPresentation.Milliseconds(0));
    }

    [Fact]
    public void Partial_active_and_safe_outcomes_remain_distinct()
    {
        var running = Stage() with { Outcome = "running", CompletedAt = null, DurationMilliseconds = null };
        Assert.Equal("Active · partial", Assert.Single(StageExecutionPresentation.Project([running], Start, true)).Outcome);
        Assert.Equal("Incomplete · partial", Assert.Single(StageExecutionPresentation.Project([running], Start, false)).Outcome);
        Assert.Equal("Completed · partial", Assert.Single(StageExecutionPresentation.Project([Stage() with { CompletedAt = null }], Start, false)).Outcome);
        foreach (var outcome in new[] { "failed", "cancelled", "skipped", "completed" })
            Assert.Equal(char.ToUpperInvariant(outcome[0]) + outcome[1..], Assert.Single(StageExecutionPresentation.Project([Stage(outcome: outcome)], Start, false)).Outcome);
        Assert.Equal("Unknown", Assert.Single(StageExecutionPresentation.Project([Stage(outcome: "secret-error")], Start, false)).Outcome);
        foreach (var code in new[] { "cancelled", "head-changed", "stage-failed", "journal-write-cancelled", "journal-write-failed" })
            Assert.Equal(code, StageExecutionPresentation.SafeFailureCode(code));
        Assert.Equal("unavailable", StageExecutionPresentation.SafeFailureCode("secret-error"));
        Assert.Equal("unavailable", StageExecutionPresentation.SafeFailureCode(null));
    }

    [Fact]
    public void Chronological_and_duration_order_is_stable_unknowns_last()
    {
        var rows = StageExecutionPresentation.Project([Stage(20, 3), Stage(null), Stage(20), Stage(10), Stage() with { StartedAt = default }], Start, false);
        Assert.Equal(new[] { 1, 2, 3, 0, 4 }, StageExecutionPresentation.Sort(rows, "chronological").Select(r => r.Index));
        Assert.Equal(new[] { 4, 0, 2, 3, 1 }, StageExecutionPresentation.Sort(rows, "duration-desc").Select(r => r.Index));
        Assert.Equal(new[] { 3, 0, 2, 4, 1 }, StageExecutionPresentation.Sort(rows, "duration-asc").Select(r => r.Index));
    }
}
