using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 7.5 (between validate and triage): persist the in-flight run shell — run row with
/// Success=false, CompletedAt=null, plus one finding row per known key (accepted this run
/// plus carried-forward prior findings, ThreadId=null). Every later external write can now
/// backfill against a durable row, and an interrupted run is visible as in-flight instead of
/// vanishing. GetLastCompletedRunAsync filters on CompletedAt != null && Success, so the
/// shell never seeds the next run's gate or dedupe.
/// </summary>
public sealed class BeginRunStage(IFindingStore store, TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "begin-run";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var acceptedKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);

        var findings = ctx.AcceptedFindings
            .Select(f => new StoredFinding(
                f.DedupeKey!, f.RuleId, f.Severity, f.Title,
                f.Anchor?.FilePath, f.Anchor?.StartLine, ThreadId: null))
            // Carry-forward from P0-1: prior keys remain known identities. ThreadId is nulled
            // here only if the prior run had none; preserve it when present.
            .Concat((ctx.PriorRun?.Findings ?? [])
                .Where(p => !acceptedKeys.Contains(p.DedupeKey)))
            .ToList();

        var run = new ReviewRun(
            ctx.RunId, ctx.Pr, ctx.PullRequest!.SourceCommitSha, ctx.Kind,
            ctx.StartedAt, CompletedAt: null, Success: false, findings);

        return store.SaveRunAsync(run, ct);
    }
}
