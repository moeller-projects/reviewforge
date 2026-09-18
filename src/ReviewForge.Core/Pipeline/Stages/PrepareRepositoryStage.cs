using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 3: local checkout as the agent's context source + unified diff of the change.</summary>
public sealed class PrepareRepositoryStage(IGitOps git, string workDirRoot, string? pat = null) : IReviewStage
{
    public string Name => "prepare-repository";

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var pr = ctx.PullRequest!;
        var workDir = Path.Combine(workDirRoot, Sanitize(ctx.Pr.RepositoryId));

        ctx.RepoDir = git.CloneOrOpen(pr.CloneUrl, workDir, pat);
        git.Checkout(ctx.RepoDir, pr.SourceCommitSha);
        ctx.DiffText = git.GetDiff(ctx.RepoDir, pr.TargetCommitSha, pr.SourceCommitSha);
        ctx.Diff = DiffIndex.Parse(ctx.DiffText);

        var providerFiles = ctx.ChangedFileManifest
            .Where(file => file.ChangeType != ChangedFileType.Delete)
            .Select(file => Normalize(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diffFiles = ctx.Diff.Files.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!providerFiles.SetEquals(diffFiles))
        {
            throw new InvalidOperationException(
                $"provider changed-file scope does not match Git diff (provider: {providerFiles.Count}, diff: {diffFiles.Count})");
        }

        return Task.CompletedTask;
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string Sanitize(string repositoryId)
        => string.Concat(repositoryId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}