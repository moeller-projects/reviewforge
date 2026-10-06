using ReviewForge.Core.AutoFix;
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
        // Checked for symmetry with stages 8/9: a cancel requested before the finalize
        // write must not persist a run the publish stages never ran.
        ct.ThrowIfCancellationRequested();

        var findings = AppliedFixPersistence.BuildFinalRows(
            ctx, key => ctx.PostedThreadIds.TryGetValue(key, out var id) ? id : null);

        // Watermark for the follow-up gate (P2-24): the newest comment timestamp observed at
        // stage-1 fetch, in ADO server time. Comments the run itself posts (stage 9) are
        // not in ctx.Fetch.Threads, so they cannot raise the watermark of their own run.
        var lastObservedComment = ctx.Fetch.Threads
            .SelectMany(t => t.Comments)
            .Select(c => (DateTimeOffset?)c.PublishedAt)
            .Max();

        var run = new ReviewRun(
            ctx.RunId,
            ctx.Pr,
            ctx.RequirePullRequest().SourceCommitSha,
            ctx.Kind,
            ctx.StartedAt,
            _Clock.GetUtcNow(),
            Success: true,
            findings,
            LastObservedCommentAt: lastObservedComment,
            Pipeline: ctx.RunKind.ToString());

        return store.SaveRunAsync(run, ct);
    }
}