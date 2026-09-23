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
    {
        // Dynamic programming bounds repeated '**' patterns to O(path × pattern)
        // instead of recursively revisiting the same split combinations.
        var previous = new bool[pattern.Length + 1];
        var current = new bool[pattern.Length + 1];
        previous[0] = true;
        for (var gi = 1; gi <= pattern.Length; gi++)
        {
            previous[gi] = pattern[gi - 1] == "**" && previous[gi - 1];
        }

        for (var pi = 1; pi <= path.Length; pi++)
        {
            for (var gi = 1; gi <= pattern.Length; gi++)
            {
                current[gi] = pattern[gi - 1] == "**"
                    ? current[gi - 1] || previous[gi]
                    : previous[gi - 1] && SegmentMatches(path[pi - 1], pattern[gi - 1]);
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return previous[pattern.Length];
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
