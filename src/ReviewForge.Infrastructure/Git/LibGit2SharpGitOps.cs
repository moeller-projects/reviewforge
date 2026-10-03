using System.Diagnostics.CodeAnalysis;
using System.Text;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Pipeline;
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

    /// <summary>Host the PAT may be presented to (from Ado:OrgUrl). Null disables the check.</summary>
    private readonly string? _CredentialHost;

    public LibGit2SharpGitOps(
        bool targetedFetch = false, GitOperationScheduler? scheduler = null, string? credentialHost = null)
    {
        _TargetedFetch = targetedFetch;
        _CredentialHost = credentialHost;
        _Scheduler = scheduler ?? new GitOperationScheduler(
            Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
    }

    public async Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct, string? mirrorPath = null)
    {
        // The mirror is shared across checkouts: hold the keyed lock for clone+fetch.
        // The wait is cancellable (real ct); the native work runs off the pool.
        var mirror = mirrorPath ?? MirrorPath(workDir);
        using var gate = await _MirrorLocks.AcquireAsync(mirror, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("mirror lock acquisition returned no lease");
        return await _Scheduler.RunAsync(() => CloneOrOpenCore(cloneUrl, workDir, pat, mirror), ct)
            .ConfigureAwait(false);
    }

    public Task<string> CommitAsync(
        string repoPath, string message, string authorName, string authorEmail,
        IReadOnlyList<string>? paths, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            if (paths is not null)
            {
                Commands.Stage(repo, paths);
            }
            else
            {
                Commands.Stage(repo, "*");
            }

            if (!repo.RetrieveStatus(new StatusOptions {IncludeIgnored = false}).IsDirty
                || !repo.Diff.Compare<TreeChanges>(repo.Head.Tip?.Tree, DiffTargets.Index).Any())
            {
                throw new InvalidOperationException("nothing to commit");
            }

            var signature = new Signature(authorName, authorEmail, DateTimeOffset.Now);
            return repo.Commit(message, signature, signature).Sha;
        }, ct);

    public async Task PushAsync(
        string repoPath, string cloneUrl, string remoteBranch, string expectedRemoteTipSha, string? pat, CancellationToken ct)
    {
        // Pre-read for error precision only; correctness comes from the server rejecting a
        // non-fast-forward ref update (the refspec carries no force flag anywhere).
        var tip = await GetRemoteTipAsync(repoPath, cloneUrl, remoteBranch, pat, ct).ConfigureAwait(false);
        if (!string.Equals(tip, expectedRemoteTipSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new PrHeadChangedException(expectedRemoteTipSha, tip ?? "(branch missing on remote)");
        }

        await _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            var remote = AuthoritativeRemote(repo, cloneUrl);
            var localRef = $"refs/heads/{remoteBranch}";
            // The checkout HEAD is detached; the push refspec needs a local ref for the new tip.
            repo.Refs.Add(localRef, repo.Head.Tip?.Id ?? throw new InvalidOperationException("no HEAD commit to push"), allowOverwrite: true);
            try
            {
                repo.Network.Push(remote, $"{localRef}:{localRef}", new PushOptions
                {
                    CredentialsProvider = Credentials(pat),
                });
            }
            finally
            {
                RemoveRef(repo, localRef);
            }

            return true;
        }, ct).ConfigureAwait(false);
    }

    public Task<string?> GetRemoteTipAsync(
        string repoPath, string cloneUrl, string remoteBranch, string? pat, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            var remote = AuthoritativeRemote(repo, cloneUrl);
            var canonical = $"refs/heads/{remoteBranch}";
            return repo.Network.ListReferences(remote, Credentials(pat))
                .FirstOrDefault(reference => string.Equals(reference.CanonicalName, canonical, StringComparison.Ordinal))
                ?.TargetIdentifier;
        }, ct);

    public Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct)
        => _Scheduler.RunAsync(() =>
        {
            using var repo = new Repository(repoPath);
            var commit = repo.Lookup<Commit>(commitSha);
            return commit is null ? null : new TipCommitInfo(commit.Author.Email, commit.Message);
        }, ct);

    /// <summary>The remote that talks to the authoritative clone URL. A checkout cloned from
    /// the local mirror has "origin" pointing at that mirror — pushes and remote-tip reads
    /// must never use it.</summary>
    private static Remote AuthoritativeRemote(Repository repo, string cloneUrl)
        => repo.Network.Remotes[AuthoritativeRemoteName]
           ?? repo.Network.Remotes.Add(AuthoritativeRemoteName, cloneUrl);

    private const string AuthoritativeRemoteName = "reviewforge-origin";

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

    public async Task WarmupMirrorAsync(string mirrorPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct)
    {
        // Same keyed lock as CloneOrOpenAsync for this mirror: warmup serializes with
        // checkout acquisition and any concurrent warmup of the same repository, and the
        // bare mirror is created here when this repo has never been checked out (the
        // pre-existing warmup bug: EnsureCommitsCore opened a mirror path that did not
        // exist yet and the failure was swallowed, making warmup a silent no-op).
        using var gate = await _MirrorLocks.AcquireAsync(mirrorPath, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("mirror lock acquisition returned no lease");
        await _Scheduler.RunAsync(() =>
        {
            if (!Repository.IsValid(mirrorPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);
                Repository.Clone(cloneUrl, mirrorPath, new CloneOptions(FetchOptions(pat)) {IsBare = true});
            }

            EnsureCommitsCore(mirrorPath, cloneUrl, baseSha, headSha, pat);
            return true;
        }, ct).ConfigureAwait(false);
    }

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
        if (repo.Network.Remotes[AuthoritativeRemoteName] is null)
        {
            repo.Network.Remotes.Add(AuthoritativeRemoteName, cloneUrl);
        }

        const string remoteName = AuthoritativeRemoteName;

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
            var textBytes = Encoding.UTF8.GetByteCount(text);
            if (textBytes > budget.MaxPerFileBytes)
            {
                var notice = $"diff --git a/{path} b/{path}\n" +
                             $"--- a/{path}\n" +
                             $"+++ b/{path}\n" +
                             $"…[file diff skipped — {textBytes} bytes exceeds the per-file budget; use repo_read_file]\n";
                TryAppendWithinBudget(sb, notice, Encoding.UTF8.GetByteCount(notice), ref total, budget.MaxTotalBytes);
                continue;
            }

            if (!TryAppendWithinBudget(sb, text, textBytes, ref total, budget.MaxTotalBytes))
            {
                var notice = $"diff --git a/{path} b/{path}\n" +
                             $"--- a/{path}\n" +
                             $"+++ b/{path}\n" +
                             "…[file diff skipped — total diff budget reached; use repo_read_file]\n";
                TryAppendWithinBudget(sb, notice, Encoding.UTF8.GetByteCount(notice), ref total, budget.MaxTotalBytes);
            }
        }

        return sb.ToString();
    }

    private static bool TryAppendWithinBudget(
        StringBuilder output,
        string text,
        int textBytes,
        ref long totalBytes,
        long maxTotalBytes)
    {
        if (totalBytes > maxTotalBytes || textBytes > maxTotalBytes - totalBytes)
        {
            return false;
        }

        output.Append(text);
        totalBytes += textBytes;
        return true;
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

    private CredentialsHandler Credentials(string? pat)
        => FetchOptions(pat).CredentialsProvider
           ?? ((_, _, _) => throw new InvalidOperationException("ADO PAT is not configured"));

    private FetchOptions FetchOptions(string? pat) => new()
    {
        CredentialsProvider = pat is null
            ? null
            : (url, _, _) =>
            {
                // Never present the PAT to a host outside the configured organization:
                // the clone URL comes from the pull-request source and the trust
                // assumption is enforced here, at the credential boundary.
                if (_CredentialHost is not null &&
                    (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                     || uri.Scheme != Uri.UriSchemeHttps
                     || !string.Equals(uri.Host, _CredentialHost, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"refusing to send the ADO PAT to '{url}' — expected host {_CredentialHost}");
                }

                return new UsernamePasswordCredentials {Username = "pat", Password = pat};
            },
    };
}
