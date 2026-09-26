using ReviewForge.Core.AutoFix;
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

    public int Order => 75;

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // Rows: accepted findings (with deterministic-fix JSON) + carry-forward prior
        // identities. Commanded-fix audit rows are added by the finalize stage, once the
        // suggestion thread ids exist.
        var findings = AppliedFixPersistence.BuildRows(ctx);

        var run = new ReviewRun(
            ctx.RunId, ctx.Pr, ctx.RequirePullRequest().SourceCommitSha, ctx.Kind,
            ctx.StartedAt, CompletedAt: null, Success: false, findings);

        return store.SaveRunAsync(run, ct);
    }
}
