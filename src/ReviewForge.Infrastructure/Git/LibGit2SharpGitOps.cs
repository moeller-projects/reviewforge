using System.Diagnostics.CodeAnalysis;
using LibGit2Sharp;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Git;

/// <summary>
/// LibGit2Sharp adapter — process-free clone/checkout/diff. Thin wrapper over the
/// library, excluded from coverage by design.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class LibGit2SharpGitOps : IGitOps
{
    public string CloneOrOpen(string cloneUrl, string workDir, string? pat)
    {
        if (Directory.Exists(Path.Combine(workDir, ".git")))
        {
            using var existing = new Repository(workDir);
            Commands.Fetch(existing, "origin", ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
            return workDir;
        }

        Directory.CreateDirectory(workDir);
        Repository.Clone(cloneUrl, workDir, new CloneOptions(FetchOptions(pat)));
        return workDir;
    }

    public void Checkout(string repoPath, string commitSha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(commitSha)
                     ?? throw new InvalidOperationException($"commit {commitSha} not found in {repoPath}");
        Commands.Checkout(repo, commit, new CheckoutOptions {CheckoutModifiers = CheckoutModifiers.Force});
    }

    public string GetDiff(string repoPath, string baseSha, string headSha)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseSha)
                         ?? throw new InvalidOperationException($"base commit {baseSha} not found");
        var headCommit = repo.Lookup<Commit>(headSha)
                         ?? throw new InvalidOperationException($"head commit {headSha} not found");

        return repo.Diff.Compare<Patch>(baseCommit.Tree, headCommit.Tree).Content;
    }

    private FetchOptions FetchOptions(string? pat) => new()
    {
        CredentialsProvider = pat is null
            ? null
            : (_, _, _) => new UsernamePasswordCredentials {Username = "pat", Password = pat},
    };
}