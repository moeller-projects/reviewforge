namespace ReviewForge.Core.Analysis;

/// <summary>
/// Directory-containment check immune to prefix collisions: a naive
/// <c>candidate.StartsWith(root)</c> accepts <c>/work/repo-evil</c> for root
/// <c>/work/repo</c>, which with per-head checkouts would expose a sibling run's tree.
/// </summary>
internal static class PathSafety
{
    public static bool IsContained(string root, string candidate)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return rel != ".."
               && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathRooted(rel);
    }
}
