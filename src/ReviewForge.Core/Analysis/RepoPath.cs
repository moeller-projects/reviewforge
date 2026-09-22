namespace ReviewForge.Core.Analysis;

/// <summary>
/// Canonical repo-relative path form: forward slashes, no leading slash. Used anywhere a
/// provider path, diff path, or finding anchor path is compared or keyed.
/// </summary>
public static class RepoPath
{
    /// <summary>Canonical form for comparison (OrdinalIgnoreCase sets carry case).</summary>
    public static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Canonical lowercased form for identity keys (dedupe keys are case-insensitive).</summary>
    public static string NormalizeKey(string path)
        => Normalize(path).ToLowerInvariant();
}