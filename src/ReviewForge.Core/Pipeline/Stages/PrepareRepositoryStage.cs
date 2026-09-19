using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 3: acquire a per-head checkout and compute the unified change diff.</summary>
public sealed class PrepareRepositoryStage(RepoCheckoutPool pool) : IReviewStage
{
    public string Name => "prepare-repository";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var pr = ctx.PullRequest!;
        var checkout = await pool.AcquireAsync(ctx.Pr.RepositoryId, pr.CloneUrl, pr.SourceCommitSha, ct);
        ctx.RepoLease = checkout;
        ctx.RepoDir = checkout.Path;
        ctx.DiffText = pool.GetDiff(ctx.RepoDir, pr.TargetCommitSha, pr.SourceCommitSha);
        ctx.Diff = DiffIndex.Parse(ctx.DiffText);

        var providerFiles = ctx.ChangedFileManifest
            .Where(file => file.ChangeType != ChangedFileType.Delete)
            .Select(file => Normalize(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diffFiles = ctx.Diff.Files.Select(file => Normalize(file)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!providerFiles.SetEquals(diffFiles))
        {
            throw new InvalidOperationException(
                $"provider changed-file scope does not match Git diff (provider: {providerFiles.Count}, diff: {diffFiles.Count})");
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
