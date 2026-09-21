namespace ReviewForge.Core.Analysis;

/// <summary>
/// Minimal gitignore-style glob matching for diff exclusions: '*' matches within a path
/// segment, '**' matches across segments. Paths are '/'-normalized, case-insensitive.
/// </summary>
public static class DiffExclusions
{
    public static bool IsExcluded(string path, IReadOnlyList<string> globs)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return globs.Any(g => Matches(normalized.Split('/'), g.Split('/')));
    }

    private static bool Matches(string[] path, string[] pattern)
        => MatchesAt(path, 0, pattern, 0);

    private static bool MatchesAt(string[] path, int pi, string[] pattern, int gi)
    {
        while (true)
        {
            if (gi == pattern.Length)
            {
                return pi == path.Length;
            }

            var seg = pattern[gi];
            if (seg == "**")
            {
                // '**' consumes zero or more path segments.
                for (var skip = 0; pi + skip <= path.Length; skip++)
                {
                    if (MatchesAt(path, pi + skip, pattern, gi + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (pi == path.Length || !SegmentMatches(path[pi], seg))
            {
                return false;
            }

            pi++;
            gi++;
        }
    }

    private static bool SegmentMatches(string text, string glob)
    {
        // '*' matches any run of non-'/' characters; no other wildcards.
        var ti = 0;
        var gi = 0;
        var star = -1;
        var starTi = 0;
        while (ti < text.Length)
        {
            if (gi < glob.Length && (glob[gi] == '*' || char.ToLowerInvariant(glob[gi]) == char.ToLowerInvariant(text[ti])))
            {
                if (glob[gi] == '*')
                {
                    star = gi++;
                    starTi = ti;
                }
                else
                {
                    gi++;
                    ti++;
                }
            }
            else if (star >= 0)
            {
                gi = star + 1;
                ti = ++starTi;
            }
            else
            {
                return false;
            }
        }

        while (gi < glob.Length && glob[gi] == '*')
        {
            gi++;
        }

        return gi == glob.Length;
    }
}
