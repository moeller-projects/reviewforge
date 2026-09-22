using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 10 (finalize): mark the in-flight run completed. Upserts the shell persisted by
/// <see cref="BeginRunStage"/> — sets Success/completion and merges finding rows with their
/// posted thread ids. Carried-forward prior findings (P0-1) are included so the known-key
/// set never decays to accepted-only. Skipped runs (gate-terminated) never reach here.
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

        // Watermark for the follow-up gate (P2-24): the newest comment timestamp observed at
        // stage-1 fetch, in ADO server time. Comments the run itself posts (stage 9) are
        // not in ctx.Threads, so they cannot raise the watermark of their own run.
        var lastObservedComment = ctx.Threads
            .SelectMany(t => t.Comments)
            .Select(c => (DateTimeOffset?)c.PublishedAt)
            .Max();

        var run = new ReviewRun(
            ctx.RunId,
            ctx.Pr,
            ctx.PullRequest!.SourceCommitSha,
            ctx.Kind,
            ctx.StartedAt,
            _Clock.GetUtcNow(),
            Success: true,
            findings,
            LastObservedCommentAt: lastObservedComment);

        return store.SaveRunAsync(run, ct);
    }
}