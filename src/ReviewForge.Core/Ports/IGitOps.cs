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
    /// <summary>Clones the repository (or reuses an existing checkout) and returns the work path.</summary>
    Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct);

    Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct);

    /// <summary>Unified diff between base and head commits, optionally bounded and pre-filtered.</summary>
    Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null);

    /// <summary>Returns the checked-out HEAD SHA, or null when no commit is available.</summary>
    Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct);

    /// <summary>Ensures both commits are present in the checkout, fetching them by SHA
    /// directly from the authoritative clone URL when targeted fetch is enabled.</summary>
    Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct);
}
