namespace ReviewForge.Core.Ports;

/// <summary>Git operations for the repository-preparation stage. Synchronous library
/// work is offloaded by the implementation; cancellation stops waiting promptly.</summary>
public interface IGitOps
{
    /// <summary>Clones the repository (or reuses an existing checkout) and returns the work path.</summary>
    Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct);

    Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct);

    /// <summary>Unified diff between base and head commits.</summary>
    Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct);

    /// <summary>Returns the checked-out HEAD SHA, or null when no commit is available.</summary>
    Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct);

    /// <summary>Ensures both commits are present in the checkout, fetching them by SHA
    /// directly from the authoritative clone URL when targeted fetch is enabled.</summary>
    Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct);
}
