using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 10: persist the completed run (head sha + finding keys feed the gate and
/// dedupe of the next run). Skipped runs (gate-terminated) are never persisted —
/// a draft skip must not mark the head as reviewed.
/// </summary>
public sealed class PersistRunStage(IFindingStore store, TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "persist-run";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var acceptedKeys = ctx.AcceptedFindings.Select(f => f.DedupeKey!).ToHashSet(StringComparer.Ordinal);

        var findings = ctx.AcceptedFindings
            .Select(f => new StoredFinding(
                f.DedupeKey!,
                f.RuleId,
                f.Severity,
                f.Title,
                f.Anchor?.FilePath,
                f.Anchor?.StartLine,
                ctx.PostedThreadIds.TryGetValue(f.DedupeKey!, out var threadId) ? threadId : null))
            // Carry prior findings forward: they are still known identities (still posted,
            // still deduped) even though they were not re-accepted this run. Prior ThreadId
            // is preserved — it still points at the live ADO thread.
            .Concat((ctx.PriorRun?.Findings ?? [])
                .Where(p => !acceptedKeys.Contains(p.DedupeKey)))
            .ToList();

        var run = new ReviewRun(
            ctx.RunId,
            ctx.Pr,
            ctx.PullRequest!.SourceCommitSha,
            ctx.Kind,
            ctx.StartedAt,
            _Clock.GetUtcNow(),
            Success: true,
            findings);

        return store.SaveRunAsync(run, ct);
    }
}