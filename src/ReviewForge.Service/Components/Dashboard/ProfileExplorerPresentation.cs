using System.Globalization;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service.Components.Dashboard;

/// <summary>A bounded page projection, not a recorder or reconstruction of retained aggregates.</summary>
public sealed record ProfileExplorerRow(Guid Id, long Sequence, string Kind, string Stage, string Profile,
    Guid? Parent, Guid? Logical, Guid? Attempt, int? AttemptNumber, string? Provider, string? Model, string? Tool,
    DateTimeOffset Start, DateTimeOffset? End, double? Duration, string Outcome, string ParentCoverage,
    long? Input, long? Output, long? Total, long? Cached, long? Reasoning, decimal? Cost, string? Currency,
    string? CostProvenance, double? Wait, double? Backoff);

public static class ProfileExplorerPresentation
{
    public const int PageSize = 50;
    public static IReadOnlyList<ProfileExplorerRow> Rows(ModelProfilePage page) => page.Calls.Select(c =>
        new ProfileExplorerRow(c.CallId, c.Sequence, "provider", c.Stage, c.Profile.ToString(), c.ParentCallId ?? c.AttemptId,
            c.LogicalCallId, c.AttemptId, c.AttemptNumber, c.Provider, c.Model, null, c.StartedAt, c.CompletedAt,
            Valid(c.DurationMilliseconds), c.Outcome, c.ParentCallId is null && c.AttemptId is null ? "unavailable" : "observed",
            c.InputTokens, c.OutputTokens, c.TotalTokens, c.CachedInputTokens, c.ReasoningTokens,
            c.Cost, c.Currency, c.CostProvenance, null, null))
        .Concat((page.Activities ?? []).Select(a => new ProfileExplorerRow(a.Id, a.Sequence, a.Kind, a.Stage,
            a.Profile.ToString(), a.ParentId, a.LogicalCallId, a.AttemptId, a.AttemptNumber, null, null, a.ToolName,
            a.StartedAt, a.CompletedAt, Valid(a.DurationMilliseconds), a.Outcome, a.ParentCoverage,
            null, null, null, null, null, null, null, null, Valid(a.GovernorWaitMilliseconds), Valid(a.BackoffMilliseconds))))
        .OrderBy(r => r.Sequence).Take(PageSize).ToArray();

    public static double? Valid(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    public static string Number(object? value) => value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : "unavailable";
    public static string Duration(double? value) => Valid(value)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable";
    public static string Identifier(Guid? value) => value?.ToString("D") ?? "unavailable";
    public static string Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unavailable";
    public static string Cost(ProfileExplorerRow row) => row.Cost is >= 0 && row.Currency is { Length: > 0 }
        && row.CostProvenance == "provider-reported" ? $"{Number(row.Cost)} {row.Currency} (provider-reported)" : "unavailable";

    public static IReadOnlyList<ProfileExplorerRow> Filter(IReadOnlyList<ProfileExplorerRow> rows,
        string search, string kind, string outcome, string model, string tool, string stage, string duration, string order)
    {
        var text = search.Trim();
        var filtered = rows.Where(r => (kind.Length == 0 || r.Kind == kind) && (outcome.Length == 0 || r.Outcome == outcome)
            && (model.Length == 0 || r.Model == model) && (tool.Length == 0 || r.Tool == tool)
            && (stage.Length == 0 || r.Stage == stage)
            && (duration == "" || (duration == "missing" ? r.Duration is null : r.Duration >= 1000))
            && (text.Length == 0 || string.Join(' ', r.Id, r.Parent, r.Logical, r.Attempt, r.Kind,
                r.Stage, StageLabels.Label(r.Stage), r.Profile, r.Provider, r.Model, r.Tool, r.Outcome)
                .Contains(text, StringComparison.OrdinalIgnoreCase)));
        return (order switch {
            "duration-desc" => filtered.OrderBy(r => r.Duration is null).ThenByDescending(r => r.Duration).ThenBy(r => r.Sequence),
            "duration-asc" => filtered.OrderBy(r => r.Duration is null).ThenBy(r => r.Duration).ThenBy(r => r.Sequence),
            _ => filtered.OrderBy(r => r.Start).ThenBy(r => r.Sequence)
        }).ToArray();
    }

    // Placement uses observed start offsets and monotonic duration. Never adds overlapping intervals.
    public static string Bar(ProfileExplorerRow row, IReadOnlyList<ProfileExplorerRow> rows)
    {
        if (row.Duration is null || row.End is null || row.End < row.Start) return "";
        var start = rows.Min(r => r.Start);
        var scale = Math.Max(1, rows.Max(r => (r.Start - start).TotalMilliseconds + (r.Duration ?? 0)));
        return FormattableString.Invariant($"margin-inline-start:{(row.Start - start).TotalMilliseconds / scale * 100:0.####}%;width:{row.Duration / scale * 100:0.####}%");
    }
}
