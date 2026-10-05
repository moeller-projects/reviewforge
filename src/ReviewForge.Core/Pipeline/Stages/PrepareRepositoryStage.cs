using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>How stage 3 materializes the workspace. Pooled (default) shares the per-head
/// checkout with sibling runs; Private gives CommitOnHead runs a run-scoped writable checkout
/// that is deleted on disposal (see RepoCheckoutPool.AcquirePrivateAsync).</summary>
public enum CheckoutMode { Pooled, Private }

/// <summary>Stage 3: acquire a per-head checkout and compute the unified change diff.
/// Also starts the stage-4 threads refresh and the stage-5 enrichment call so both round-trips
/// overlap the clone/fetch window; the stage-order contract is untouched — stages 4 and 5
/// consume the in-flight tasks with the same freshness and fail-safe semantics as before.
/// Loop guard: after checkout the head commit's author/message is read (cheap local lookup);
/// a discovery-triggered run on a verifiably bot-authored head terminates here — the review
/// gate (order 20) runs before the checkout exists, so the rule cannot live there.</summary>
public sealed class PrepareRepositoryStage(
    RepoCheckoutPool pool,
    ILogger<PrepareRepositoryStage> logger,
    DiffBudget? diffBudget = null,
    IPullRequestSource? source = null,
    IContextEnricher? enricher = null,
    TimeProvider? clock = null,
    CheckoutMode checkoutMode = CheckoutMode.Pooled,
    AutoFixOptions? autoFix = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "prepare-repository";

    public int Order => 30;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // The refresh is launched before the clone begins. Its token is linked with the
        // context's overlap CTS so a failed run's Dispose cancels it; the completion
        // continuation stamps CompletedAt for stage 4's freshness predicate and observes
        // any fault for runs that never reach stage 4.
        if (source is not null)
        {
            var overlap = new ThreadsRefreshOverlap(
                source.GetThreadsAsync(ctx.Pr, LinkOverlapToken(ct, ctx.OverlapCts.Token)),
                _Clock.GetUtcNow());
            ctx.PendingThreadsRefresh = overlap;
            _ = ObserveCompletionAsync(overlap);
        }

        var pr = ctx.RequirePullRequest();
        var checkout = checkoutMode == CheckoutMode.Private
            ? await pool.AcquirePrivateAsync(ctx.RunId, ctx.Pr.RepositoryId, pr.CloneUrl, pr.TargetCommitSha, pr.SourceCommitSha, ct).ConfigureAwait(false)
            : await pool.AcquireAsync(ctx.Pr.RepositoryId, pr.CloneUrl, pr.TargetCommitSha, pr.SourceCommitSha, ct).ConfigureAwait(false);
        ctx.RepoLease = checkout;
        ctx.RepoDir = checkout.Path;

        // Loop-guard input: cheap local lookup, filled for every run so the gate rule and
        // tests observe the same value. Null (commit not found) reads as "proceed".
        ctx.HeadCommitInfo = await pool.GetCommitInfoAsync(ctx.RepoDir, pr.SourceCommitSha, ct).ConfigureAwait(false);
        if (ctx.Trigger == EnqueueTrigger.Discovery
            && ctx.HeadCommitInfo is { } headInfo
            && autoFix is not null
            && LoopGuard.IsBotAuthoredHead(headInfo, autoFix))
        {
            ReviewForgeTelemetry.LoopGuardSkips.Add(
                1, new TagList { { "source", "gate" } });
            logger.LogInformation(
                "loop guard: discovery-triggered run on bot-authored head {Sha} suppressed (run {RunId})",
                pr.SourceCommitSha, ctx.RunId);
            ctx.Terminate("bot-authored head");
            return;
        }

        ctx.DiffText = await pool.GetDiffAsync(ctx.RepoDir, pr.TargetCommitSha, pr.SourceCommitSha, ct, diffBudget).ConfigureAwait(false);
        ctx.Diff = DiffIndex.Parse(ctx.DiffText);

        var nonReviewable = ctx.Diff.NonReviewableFiles.Keys.ToHashSet(RepoPath.PathComparer);
        var reviewable = new HashSet<string>(RepoPath.PathComparer);
        foreach (var file in ctx.ChangedFileManifest.Where(f => f.ChangeType != ChangedFileType.Delete))
        {
            var path = RepoPath.Normalize(file.Path);
            if (nonReviewable.Contains(path))
            {
                logger.LogInformation("excluding non-reviewable file {Path} ({Kind}) from review scope",
                    path, ctx.Diff.NonReviewableFiles[path]);
                continue;
            }

            if (diffBudget is not null && DiffExclusions.IsExcluded(path, diffBudget.ExcludeGlobs))
            {
                // Machine-generated content — never reviewable, never in the diff. The only
                // legitimate way a manifest file has no diff entry.
                logger.LogDebug("excluding budget-excluded file {Path} from review scope", path);
                continue;
            }

            reviewable.Add(path);
        }

        var diffFiles = ctx.Diff.Files.Select(RepoPath.Normalize).ToHashSet(RepoPath.PathComparer);
        var missingFromDiff = reviewable.Where(f => !diffFiles.Contains(f)).Order(RepoPath.PathComparer).ToArray();
        if (missingFromDiff.Length > 0)
        {
            // Fail closed (P1-14): a manifest file with no diff entry means quoting/parsing
            // dropped it — reviewing a silently shrunk scope is worse than failing the run.
            throw new InvalidOperationException(
                $"provider changed-file manifest has no diff entry for: {string.Join(", ", missingFromDiff)}. " +
                "Refusing to shrink review scope silently.");
        }

        if (!reviewable.SetEquals(diffFiles))
        {
            throw new InvalidOperationException(
                $"provider changed-file scope does not match Git diff (provider: {reviewable.Count}, diff: {diffFiles.Count})");
        }

        ctx.ReviewableFiles = reviewable;
        ctx.RepoPreparedAt = _Clock.GetUtcNow();

        // Enrichment needs only RepoDir + DiffText, both final now; stage 5 awaits the task
        // with its usual fail-safe catch. A contract-violating synchronous throw is captured
        // into the task so stage 5 handles it identically to an async failure.
        if (enricher is not null && ctx.RepoDir is not null)
        {
            try
            {
                ctx.PendingEnrichment = enricher.EnrichAsync(
                    ctx.RepoDir, ctx.DiffText, LinkOverlapToken(ct, ctx.OverlapCts.Token));
            }
            catch (Exception ex)
            {
                ctx.PendingEnrichment = Task.FromException<string?>(ex);
            }
        }
    }

    private async Task ObserveCompletionAsync(ThreadsRefreshOverlap overlap)
    {
        try
        {
            await overlap.Task.ConfigureAwait(false);
            overlap.CompletedAt = _Clock.GetUtcNow();
        }
        catch
        {
            // Observed here so a run that never reaches stage 4 never surfaces an
            // unobserved-task exception; ClassifyRunStage catches the rethrown await and
            // falls back to the serial refetch.
        }
    }

    /// <summary>Links the run token with the context's overlap token so either a run-wide
    /// cancellation or context disposal stops the overlap work. The linked source is
    /// deliberately not disposed: it is one small allocation per run, and disposing while
    /// the consumer's registration is live is a worse failure mode than GC reclamation.</summary>
    private static CancellationToken LinkOverlapToken(CancellationToken runToken, CancellationToken overlapToken)
        => CancellationTokenSource.CreateLinkedTokenSource(runToken, overlapToken).Token;
}
