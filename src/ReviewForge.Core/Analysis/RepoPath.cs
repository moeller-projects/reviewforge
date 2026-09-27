namespace ReviewForge.Core.Analysis;

/// <summary>
/// Canonical repo-relative path form: forward slashes, no leading slash. Used anywhere a
/// provider path, diff path, or finding anchor path is compared or keyed.
/// </summary>
public static class RepoPath
{
    /// <summary>Comparer matching the host filesystem's normal path identity rules.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Comparison matching <see cref="PathComparer"/>.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Canonical form for comparison: forward slashes and no leading slash.</summary>
    public static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Canonical lowercased form for identity keys (dedupe keys are case-insensitive).</summary>
    public static string NormalizeKey(string path)
        => Normalize(path).ToLowerInvariant();
}