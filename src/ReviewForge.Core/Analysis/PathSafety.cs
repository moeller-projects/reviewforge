namespace ReviewForge.Core.Analysis;

/// <summary>
/// Directory-containment check immune to prefix collisions: a naive
/// <c>candidate.StartsWith(root)</c> accepts <c>/work/repo-evil</c> for root
/// <c>/work/repo</c>, which with per-head checkouts would expose a sibling run's tree.
/// </summary>
internal static class PathSafety
{
    /// <summary>Fully resolves symlinks/junctions in <paramref name="path"/> and returns the
    /// real path. Does NOT check containment — pair with <see cref="PathContainment.IsContained"/>.</summary>
    public static string ResolveReal(string path) => ResolveLinks(path);

    public static bool IsContainedReal(string root, string candidate)
        => PathContainment.IsContained(ResolveReal(root), ResolveReal(candidate));


    private static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;
            if (info?.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return current;
            }

            current = target.FullName;
        }

        return current;
    }
}