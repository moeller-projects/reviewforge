using System.Diagnostics.CodeAnalysis;
using LibGit2Sharp;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Infrastructure.Git;

/// <summary>
/// LibGit2Sharp adapter — process-free clone/checkout/diff. Thin wrapper over the
/// library, excluded from coverage by design.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class LibGit2SharpGitOps : IGitOps
{
    private readonly KeyedLockPool _MirrorLocks = new();

    public string CloneOrOpen(string cloneUrl, string workDir, string? pat)
    {
        var mirror = EnsureMirror(cloneUrl, workDir, pat);
        if (Directory.Exists(Path.Combine(workDir, ".git")))
        {
            using var existing = new Repository(workDir);
            Commands.Fetch(existing, "origin", ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
            return workDir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workDir)!);
        Repository.Clone(mirror, workDir, new CloneOptions());
        return workDir;
    }

    private string EnsureMirror(string cloneUrl, string workDir, string? pat)
    {
        var mirror = MirrorPath(workDir);
        using var gate = _MirrorLocks.AcquireAsync(mirror, CancellationToken.None)
            .GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("mirror lock acquisition returned no lease");
        if (Repository.IsValid(mirror))
        {
            using var existing = new Repository(mirror);
            Commands.Fetch(existing, "origin", ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            Repository.Clone(cloneUrl, mirror, new CloneOptions(FetchOptions(pat)) {IsBare = true});
        }

        return mirror;
    }

    internal static string MirrorPath(string workDir)
    {
        var full = Path.GetFullPath(workDir);
        var parent = Directory.GetParent(full)?.FullName
                     ?? throw new ArgumentException("work directory must have a parent", nameof(workDir));
        var checkoutRoot = Directory.GetParent(parent);
        if (checkoutRoot is not null &&
            string.Equals(Path.GetFileName(checkoutRoot.FullName), "checkouts", StringComparison.Ordinal))
        {
            return Path.Combine(checkoutRoot.Parent!.FullName, "mirror", Path.GetFileName(parent));
        }

        return Path.Combine(parent, "mirror", Path.GetFileName(full));
    }

    public void Checkout(string repoPath, string commitSha)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(commitSha)
                     ?? throw new InvalidOperationException($"commit {commitSha} not found in {repoPath}");
        Commands.Checkout(repo, commit, new CheckoutOptions {CheckoutModifiers = CheckoutModifiers.Force});
    }

    public string? GetHeadSha(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Head.Tip?.Sha;
    }

    public void FetchCommits(string repoPath, string? pat, IReadOnlyList<string> refSpecs)
    {
        using var repo = new Repository(repoPath);
        Commands.Fetch(repo, "origin", refSpecs, FetchOptions(pat), null);
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