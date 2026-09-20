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
    private readonly bool _TargetedFetch;

    public LibGit2SharpGitOps(bool targetedFetch = false)
    {
        _TargetedFetch = targetedFetch;
    }

    public string CloneOrOpen(string cloneUrl, string workDir, string? pat)
    {
        var mirror = EnsureMirror(cloneUrl, workDir, pat);
        if (Directory.Exists(Path.Combine(workDir, ".git")))
        {
            if (!_TargetedFetch)
            {
                using var existing = new Repository(workDir);
                Commands.Fetch(existing, "origin", ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
            }

            return workDir;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workDir)!);
        Repository.Clone(mirror, workDir, new CloneOptions());
        return workDir;
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

    public void EnsureCommits(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat)
    {
        if (!_TargetedFetch)
        {
            return;
        }

        using var repo = new Repository(repoPath);
        if (HasCommit(repo, baseSha) && HasCommit(repo, headSha))
        {
            return;
        }

        // The checkout's "origin" remote points at the local mirror; fetch the SHAs from
        // the authoritative clone URL so forks and PR refs resolve regardless of namespace.
        const string remoteName = "reviewforge-origin";
        if (repo.Network.Remotes[remoteName] is null)
        {
            repo.Network.Remotes.Add(remoteName, cloneUrl);
        }

        try
        {
            Commands.Fetch(repo, remoteName,
                [$"+{baseSha}:refs/reviewforge/base", $"+{headSha}:refs/reviewforge/head"],
                FetchOptions(pat), null);
        }
        catch (LibGit2SharpException ex)
        {
            // Some ADO configurations reject fetch-by-SHA. Fall back to all heads, but only
            // succeed if the fallback actually reached the commits — otherwise the run must
            // fail visibly (no silent degradation) rather than mask the real fetch error.
            Commands.Fetch(repo, remoteName, ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
            if (!HasCommit(repo, baseSha) || !HasCommit(repo, headSha))
            {
                throw new InvalidOperationException(
                    $"failed to fetch {baseSha}..{headSha} into {repoPath}: fetch-by-SHA was rejected and the heads fallback did not reach the commits", ex);
            }
        }
        finally
        {
            RemoveRef(repo, "refs/reviewforge/base");
            RemoveRef(repo, "refs/reviewforge/head");
        }
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

    private string EnsureMirror(string cloneUrl, string workDir, string? pat)
    {
        var mirror = MirrorPath(workDir);
        using var gate = _MirrorLocks.AcquireAsync(mirror, CancellationToken.None)
                             .GetAwaiter().GetResult()
                         ?? throw new InvalidOperationException("mirror lock acquisition returned no lease");
        if (Repository.IsValid(mirror))
        {
            if (!_TargetedFetch)
            {
                using var existing = new Repository(mirror);
                Commands.Fetch(existing, "origin", ["+refs/heads/*:refs/remotes/origin/*"], FetchOptions(pat), null);
            }
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

    private static bool HasCommit(Repository repo, string sha)
        => repo.Lookup<Commit>(sha) is not null;

    private static void RemoveRef(Repository repo, string name)
    {
        if (repo.Refs[name] is not null)
        {
            repo.Refs.Remove(name);
        }
    }

    private FetchOptions FetchOptions(string? pat) => new()
    {
        CredentialsProvider = pat is null
            ? null
            : (_, _, _) => new UsernamePasswordCredentials {Username = "pat", Password = pat},
    };
}