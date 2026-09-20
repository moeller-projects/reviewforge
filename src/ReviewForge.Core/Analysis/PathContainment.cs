namespace ReviewForge.Core.Analysis;

public static class PathContainment
{
    public static bool IsContained(string root, string candidate)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return rel.Length != 0
               && rel != ".."
               && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !rel.StartsWith("../", StringComparison.Ordinal)
               && !Path.IsPathRooted(rel);
    }
}