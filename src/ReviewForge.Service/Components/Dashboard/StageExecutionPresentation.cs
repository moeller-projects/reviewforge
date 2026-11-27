using System.Globalization;
using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Components.Dashboard;

public sealed record StageExecutionRow(int Index, StageExecutionDiagnostic Stage, double? OffsetMilliseconds,
    double? DurationMilliseconds, string Outcome, bool HasBar);

/// <summary>Presentation-only arithmetic. Never infers stage execution from a pipeline plan.</summary>
public static class StageExecutionPresentation
{
    public static IReadOnlyList<StageExecutionRow> Project(IReadOnlyList<StageExecutionDiagnostic> stages, DateTimeOffset? runStart, bool running) =>
        stages.Select((stage, index) =>
        {
            var offset = QueueWait(runStart, stage.StartedAt);
            var duration = stage.DurationMilliseconds is { } ms && double.IsFinite(ms) && ms >= 0 ? ms : (double?)null;
            var outcome = stage.Outcome switch
            {
                "running" => running ? "Active · partial" : "Incomplete · partial",
                "completed" => "Completed",
                "failed" => "Failed",
                "cancelled" => "Cancelled",
                "skipped" => "Skipped",
                _ => "Unknown",
            };
            var timestampsValid = stage.StartedAt != default && stage.CompletedAt is { } end && end >= stage.StartedAt;
            if (stage.Outcome != "running" && (!timestampsValid || duration is null)) outcome += " · partial";
            return new StageExecutionRow(index, stage, offset, duration, outcome,
                offset is not null && duration is not null && timestampsValid);
        }).ToArray();

    public static double? QueueWait(DateTimeOffset? enqueued, DateTimeOffset? started) =>
        enqueued is { } from && started is { } to && from != default && to != default && to >= from
            ? (to - from).TotalMilliseconds : null;

    public static string Milliseconds(double? value) => value is { } ms
        ? ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms" : "unavailable";

    public static string SafeFailureCode(string? value) => value is
        "cancelled" or "head-changed" or "stage-failed" or "journal-write-cancelled" or "journal-write-failed"
            ? value : "unavailable";

    public static IEnumerable<StageExecutionRow> Sort(IReadOnlyList<StageExecutionRow> rows, string order) => order switch
    {
        "duration-desc" => rows.OrderBy(r => r.DurationMilliseconds is null).ThenByDescending(r => r.DurationMilliseconds).ThenBy(r => r.Index),
        "duration-asc" => rows.OrderBy(r => r.DurationMilliseconds is null).ThenBy(r => r.DurationMilliseconds).ThenBy(r => r.Index),
        _ => rows.OrderBy(r => r.Stage.StartedAt == default).ThenBy(r => r.Stage.StartedAt).ThenBy(r => r.Index),
    };
}
