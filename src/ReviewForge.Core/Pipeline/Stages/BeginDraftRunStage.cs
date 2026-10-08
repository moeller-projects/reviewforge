using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Persists a draft run shell after its gate passes, so queued/running and crashed
/// drafts remain distinguishable from unknown run IDs. Gate-terminated runs never reach it.</summary>
public sealed class BeginDraftRunStage(
    IFindingStore store,
    ILogger<BeginDraftRunStage>? logger = null) : IReviewStage
{
    public string Name => "begin-draft-run";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var headSha = ctx.RequirePullRequest().SourceCommitSha;
        var observedComment = ctx.Fetch.Threads.SelectMany(t => t.Comments)
            .Select(c => (DateTimeOffset?) c.PublishedAt).Max();
        var shell = new ReviewRun(
            ctx.RunId, ctx.Pr, headSha, ReviewKind.Full,
            ctx.StartedAt, CompletedAt: null, Success: false, [],
            LastObservedCommentAt: observedComment,
            Pipeline: RunKind.ReviewDraft.ToString());
        logger?.LogDebug("persisted review draft shell {RunId} for {Pr}", ctx.RunId, ctx.Pr);
        return store.SaveRunAsync(shell, ct);
    }
}