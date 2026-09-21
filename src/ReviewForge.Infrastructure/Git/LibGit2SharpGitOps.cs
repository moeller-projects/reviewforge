using System.Diagnostics.CodeAnalysis;
using System.Text;
using LibGit2Sharp;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Infrastructure.Git;

/// <summary>
/// LibGit2Sharp adapter — process-free clone/checkout/diff. Thin wrapper over the
/// library, excluded from coverage by design. Synchronous library work runs on a
/// dedicated bounded scheduler; the mirror-lock wait honors the caller's cancellation token.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class LibGit2SharpGitOps : IGitOps
{
    private readonly KeyedLockPool _MirrorLocks = new();
    private readonly GitOperationScheduler _Scheduler;
    private readonly bool _TargetedFetch;

    public LibGit2SharpGitOps(bool targetedFetch = false, GitOperationScheduler? scheduler = null)
    {
        _TargetedFetch = targetedFetch;
        _Scheduler = scheduler ?? new GitOperationScheduler(
            Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    }

    public async Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct)
    {
        // The mirror is shared across checkouts: hold the keyed lock for clone+fetch.
        // The wait is cancellable (real ct); the native work runs off the pool.
        var mirror = MirrorPath(workDir);
        using var gate = await _MirrorLocks.AcquireAsync(mirror, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("mirror lock acquisition returned no lease");
        return await _Scheduler.RunAsync(() => CloneOrOpenCore(cloneUrl, workDir, pat, mirror), ct)
            .ConfigureAwait(false);
    }

    public Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            var commit = repo.Lookup<Commit>(commitSha)
                         ?? throw new InvalidOperationException($"commit {commitSha} not found in {repoPath}");
            Commands.Checkout(repo, commit, new CheckoutOptions {CheckoutModifiers = CheckoutModifiers.Force});
            return true;
        }, ct);

    public Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            return repo.Head.Tip?.Sha;
        }, ct);

    public Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            EnsureCommitsCore(repoPath, cloneUrl, baseSha, headSha, pat);
            return true;
        }, ct);

    public Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
        => _Scheduler.RunAsync(() => GetDiffCore(repoPath, baseSha, headSha, budget), ct);

    /// <summary>Existing CloneOrOpen body; mirror path supplied by the async wrapper.</summary>
    private string CloneOrOpenCore(string cloneUrl, string workDir, string? pat, string mirror)
    {
        // EnsureMirror body, minus the lock acquisition (now held by the caller):
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

    private void EnsureCommitsCore(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat)
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

    private string GetDiffCore(string repoPath, string baseSha, string headSha, DiffBudget? budget)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseSha)
                         ?? throw new InvalidOperationException($"base commit {baseSha} not found");
        var headCommit = repo.Lookup<Commit>(headSha)
                         ?? throw new InvalidOperationException($"head commit {headSha} not found");

        var patch = repo.Diff.Compare<Patch>(baseCommit.Tree, headCommit.Tree);
        if (budget is null)
        {
            return patch.Content;
        }

        var sb = new StringBuilder();
        long total = 0;
        foreach (var entry in patch)
        {
            var path = entry.Path.Replace('\\', '/');
            if (DiffExclusions.IsExcluded(path, budget.ExcludeGlobs))
            {
                continue; // machine-generated content — never reviewable
            }

            var text = entry.Patch ?? string.Empty;
            if (text.Length > budget.MaxPerFileBytes)
            {
                sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
                  .Append("--- a/").Append(path).Append('\n')
                  .Append("+++ b/").Append(path).Append('\n')
                  .Append("…[file diff skipped — ").Append(text.Length).Append(" bytes exceeds the per-file budget; use repo_read_file]\n");
                continue;
            }

            if (total + text.Length > budget.MaxTotalBytes)
            {
                sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
                  .Append("--- a/").Append(path).Append('\n')
                  .Append("+++ b/").Append(path).Append('\n')
                  .Append("…[file diff skipped — total diff budget reached; use repo_read_file]\n");
                continue;
            }

            sb.Append(text);
            total += text.Length;
        }

        return sb.ToString();
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
