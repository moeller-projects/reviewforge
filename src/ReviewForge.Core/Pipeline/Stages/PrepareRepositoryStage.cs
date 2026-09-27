using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 3: acquire a per-head checkout and compute the unified change diff.
/// Also starts the stage-4 threads refresh and the stage-5 enrichment call so both round-trips
/// overlap the clone/fetch window; the stage-order contract is untouched — stages 4 and 5
/// consume the in-flight tasks with the same freshness and fail-safe semantics as before.</summary>
public sealed class PrepareRepositoryStage(
    RepoCheckoutPool pool,
    ILogger<PrepareRepositoryStage> logger,
    DiffBudget? diffBudget = null,
    IPullRequestSource? source = null,
    IContextEnricher? enricher = null,
    TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public string Name => "prepare-repository";

    public int Order => 30;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // The refresh is launched before the clone begins; stage 4 trusts it only when this
        // start stamp predates the preparation stamp (see ClassifyRunStage).
        if (source is not null)
        {
            ctx.PendingThreadsRefresh = (source.GetThreadsAsync(ctx.Pr, ct), _Clock.GetUtcNow());
        }

        var pr = ctx.RequirePullRequest();
        var checkout = await pool.AcquireAsync(ctx.Pr.RepositoryId, pr.CloneUrl, pr.TargetCommitSha, pr.SourceCommitSha, ct).ConfigureAwait(false);
        ctx.RepoLease = checkout;
        ctx.RepoDir = checkout.Path;
        ctx.DiffText = await pool.GetDiffAsync(ctx.RepoDir, pr.TargetCommitSha, pr.SourceCommitSha, ct, diffBudget).ConfigureAwait(false);
        ctx.Diff = DiffIndex.Parse(ctx.DiffText);

        var nonReviewable = ctx.Diff.NonReviewableFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        var diffFiles = ctx.Diff.Files.Select(RepoPath.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFromDiff = reviewable.Where(f => !diffFiles.Contains(f)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
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
                ctx.PendingEnrichment = enricher.EnrichAsync(ctx.RepoDir, ctx.DiffText, ct);
            }
            catch (Exception ex)
            {
                ctx.PendingEnrichment = Task.FromException<string?>(ex);
            }
        }
    }

    }
