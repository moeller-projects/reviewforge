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
    public static string ResolveReal(string path) => ResolveLinks(path, out _);

    /// <summary>As <see cref="ResolveReal"/>, additionally reporting how many symlink/junction
    /// components resolution traversed (used to refuse reads through committed links).</summary>
    public static string ResolveReal(string path, out int linksTraversed) => ResolveLinks(path, out linksTraversed);

    public static bool IsContainedReal(string root, string candidate)
        => PathContainment.IsContained(ResolveReal(root), ResolveReal(candidate));


    private static string ResolveLinks(string path, out int linksTraversed)
    {
        linksTraversed = 0;
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

            linksTraversed++;
            current = target.FullName;
        }

        return current;
    }
}