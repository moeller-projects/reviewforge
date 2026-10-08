using System.Diagnostics.CodeAnalysis;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Ports;

/// <summary>Budget and exclusions applied while materializing a unified diff.</summary>
public sealed record DiffBudget(
    long MaxTotalBytes,
    int MaxPerFileBytes,
    IReadOnlyList<string> ExcludeGlobs)
{
    public static readonly DiffBudget Default = new(
        MaxTotalBytes: 4 * 1024 * 1024,
        MaxPerFileBytes: 256 * 1024,
        ExcludeGlobs:
        [
            "**/package-lock.json", "**/packages.lock.json", "**/yarn.lock", "**/pnpm-lock.yaml",
            "**/Cargo.lock", "**/go.sum", "**/*.designer.cs", "**/*.g.cs",
            "**/*.min.js", "**/*.min.css",
        ]);
}

/// <summary>Git operations for the repository-preparation stage. Synchronous library
/// work is offloaded by the implementation; cancellation stops waiting promptly.</summary>
public interface IGitOps
{
    /// <summary>Clones the repository (or reuses an existing checkout) and returns the work path.
    /// <paramref name="mirrorPath"/> overrides the derived shared-mirror location — required for
    /// run-scoped private checkouts whose path layout the derivation cannot recognize.</summary>
    Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct, string? mirrorPath = null);

    Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct);

    /// <summary>Unified diff between base and head commits, optionally bounded and pre-filtered.</summary>
    Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null);

    /// <summary>Returns the common ancestor of two commits, used as the comparison base for PR diffs.</summary>
    Task<string> GetMergeBaseShaAsync(string repoPath, string firstSha, string secondSha, CancellationToken ct);

    /// <summary>Returns the checked-out HEAD SHA, or null when no commit is available.</summary>
    Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct);

    /// <summary>Ensures both commits are present in the checkout, fetching them by SHA
    /// directly from the authoritative clone URL when targeted fetch is enabled.</summary>
    Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct);

    /// <summary>Commits changes; returns the new commit SHA. When <paramref name="paths"/> is
    /// non-null, only those repo-relative paths are staged (per-group commits); null stages the
    /// whole worktree. Author/committer identity is supplied by the caller — never derived from
    /// config inside the adapter. Throws <see cref="InvalidOperationException"/> with a
    /// "nothing to commit" message when staging produced no changes.</summary>
    Task<string> CommitAsync(
        string repoPath, string message, string authorName, string authorEmail,
        IReadOnlyList<string>? paths, CancellationToken ct);

    /// <summary>Fast-forward-only push of HEAD to refs/heads/{remoteBranch} on the authoritative
    /// remote (<paramref name="cloneUrl"/>) with compare-and-swap semantics: the ref update is
    /// rejected unless its current value EQUALS <paramref name="expectedRemoteTipSha"/> — a plain
    /// non-fast-forward check is insufficient (a force-reset to an ancestor of the pinned head
    /// would still fast-forward). The implementation re-reads the remote tip immediately before
    /// the ref update and throws <see cref="PrHeadChangedException"/> on any movement; a movement
    /// inside that final window is caught by the server's non-fast-forward rejection and surfaced
    /// as-is. Never forces. The checkout's own "origin" is never pushed to — it may be a local
    /// mirror.</summary>
    Task PushAsync(
        string repoPath, string cloneUrl, string remoteBranch, string expectedRemoteTipSha, string? pat, CancellationToken ct);

    /// <summary>Current tip SHA of refs/heads/{remoteBranch} on the authoritative remote
    /// (<paramref name="cloneUrl"/>; ls-remote equivalent, no local ref mutation), or null when
    /// the branch doesn't exist.</summary>
    Task<string?> GetRemoteTipAsync(
        string repoPath, string cloneUrl, string remoteBranch, string? pat, CancellationToken ct);

    /// <summary>Author email + full message of the given commit, for the loop guard.
    /// Null when the commit isn't present locally.</summary>
    Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct);

    /// <summary>Speculative prefetch of base/head commits into the shared bare mirror so a
    /// later acquire skips the origin fetch. The mirror may not exist yet — implementations
    /// create it under the SAME per-mirror lock that guards checkout acquisition, so warmup
    /// never races a clone/fetch of the same repository. Warmup is an optimization: failure
    /// must not affect the run, whose own clone/fetch remains the correctness path.</summary>
    Task WarmupMirrorAsync(string mirrorPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct);
}

/// <summary>Loop-guard input: the head commit's self-asserted author email and full message.</summary>
[ExcludeFromCodeCoverage]
public sealed record TipCommitInfo(string AuthorEmail, string Message);