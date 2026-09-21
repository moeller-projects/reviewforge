using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 3: acquire a per-head checkout and compute the unified change diff.</summary>
public sealed class PrepareRepositoryStage(
    RepoCheckoutPool pool,
    ILogger<PrepareRepositoryStage> logger,
    DiffBudget? diffBudget = null) : IReviewStage
{
    public string Name => "prepare-repository";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var pr = ctx.PullRequest!;
        var checkout = await pool.AcquireAsync(ctx.Pr.RepositoryId, pr.CloneUrl, pr.TargetCommitSha, pr.SourceCommitSha, ct).ConfigureAwait(false);
        ctx.RepoLease = checkout;
        ctx.RepoDir = checkout.Path;
        ctx.DiffText = await pool.GetDiffAsync(ctx.RepoDir, pr.TargetCommitSha, pr.SourceCommitSha, ct, diffBudget).ConfigureAwait(false);
        ctx.Diff = DiffIndex.Parse(ctx.DiffText);

        var nonReviewable = ctx.Diff.NonReviewableFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reviewable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ctx.ChangedFileManifest.Where(f => f.ChangeType != ChangedFileType.Delete))
        {
            var path = Normalize(file.Path);
            if (nonReviewable.Contains(path))
            {
                logger.LogInformation("excluding non-reviewable file {Path} ({Kind}) from review scope",
                    path, ctx.Diff.NonReviewableFiles[path]);
                continue;
            }

            if (diffBudget is not null && DiffExclusions.IsExcluded(path, diffBudget.ExcludeGlobs))
            {
                continue; // machine-generated content — never reviewable, never in the diff
            }

            reviewable.Add(path);
        }

        var diffFiles = ctx.Diff.Files.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFromDiff = reviewable.Where(f => !diffFiles.Contains(f)).ToArray();
        foreach (var orphan in missingFromDiff)
        {
            // Diff is authoritative: review what git actually shows, never poison the PR.
            logger.LogWarning("provider manifest file {Path} has no diff entry; excluding from scope", orphan);
            reviewable.Remove(orphan);
        }

        if (!reviewable.SetEquals(diffFiles))
        {
            throw new InvalidOperationException(
                $"provider changed-file scope does not match Git diff (provider: {reviewable.Count}, diff: {diffFiles.Count})");
        }

        ctx.ReviewableFiles = reviewable;
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
