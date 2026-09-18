namespace ReviewForge.Core.Ports;

/// <summary>Git operations for the repository-preparation stage.</summary>
public interface IGitOps
{
    /// <summary>Clones the repository (or reuses an existing checkout) and returns the work path.</summary>
    string CloneOrOpen(string cloneUrl, string workDir, string? pat);

    void Checkout(string repoPath, string commitSha);

    /// <summary>Unified diff between base and head commits.</summary>
    string GetDiff(string repoPath, string baseSha, string headSha);
}