namespace ReviewForge.Core.Ports;

/// <summary>Git operations for the repository-preparation stage.</summary>
public interface IGitOps
{
    /// <summary>Clones the repository (or reuses an existing checkout) and returns the work path.</summary>
    string CloneOrOpen(string cloneUrl, string workDir, string? pat);

    void Checkout(string repoPath, string commitSha);

    /// <summary>Unified diff between base and head commits.</summary>
    string GetDiff(string repoPath, string baseSha, string headSha);

    /// <summary>Returns the checked-out HEAD SHA, or null when no commit is available.</summary>
    string? GetHeadSha(string repoPath);

    /// <summary>
    /// Ensures both commits are present in the checkout, fetching them by SHA directly
    /// from the authoritative clone URL when targeted fetch is enabled.
    /// </summary>
    void EnsureCommits(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat);
}