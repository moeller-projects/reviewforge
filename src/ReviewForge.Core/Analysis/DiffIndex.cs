using System.Text;

namespace ReviewForge.Core.Analysis;

public enum DiffEntryKind
{
    Text,
    Binary,
    ModeOnly,
    RenameOnly
}

/// <summary>
/// Parsed unified diff: which lines of which files are part of the change. Built once in
/// repository preparation, consumed by anchor validation. Added lines are coalesced into
/// sorted, non-overlapping ranges at parse time so lookups are binary searches. Non-text
/// sections (binary, mode-only, content-free rename) are classified so the scope guard can
/// exclude them.
/// </summary>
public sealed class DiffIndex
{
    private readonly Dictionary<string, List<(int Start, int End)>> _ChangedLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiffEntryKind> _NonReviewable = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _ChangedLines.Keys;

    /// <summary>Files present in the diff that carry no reviewable text content
    /// (binary, mode-only, content-free rename), keyed by destination path.</summary>
    public IReadOnlyDictionary<string, DiffEntryKind> NonReviewableFiles => _NonReviewable;

    public static DiffIndex Parse(string unifiedDiff)
    {
        var index = new DiffIndex();
        string? currentFile = null;   // file registered via "+++ b/"
        string? sectionFile = null;   // destination path of the current "diff --git" section
        var sawContent = false;       // section produced a +++ b/ header
        var sectionBinary = false;
        var sectionRename = false;
        var newLine = 0;
        var inHunk = false;
        var runStart = 0;
        var runEnd = 0; // runEnd < runStart means no open run

        void FlushRun()
        {
            if (currentFile is not null && runEnd >= runStart)
            {
                index._ChangedLines[currentFile].Add((runStart, runEnd));
            }

            runEnd = runStart - 1;
        }

        void FlushSection()
        {
            if (sectionFile is not null && !sawContent)
            {
                var kind = sectionBinary ? DiffEntryKind.Binary
                    : sectionRename ? DiffEntryKind.RenameOnly
                    : DiffEntryKind.ModeOnly;
                index._NonReviewable[sectionFile] = kind;
            }
        }

        foreach (var rawLine in unifiedDiff.AsSpan().EnumerateLines())
        {
            var line = rawLine;

            // A "diff --git" header is a hard section boundary: recognized even when the
            // previous file's hunk is still open (real git emits it right after hunk content).
            if (line.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                FlushRun();
                FlushSection();
                sectionFile = DiffGitNewPath(line);
                sawContent = false;
                sectionBinary = false;
                sectionRename = false;
                currentFile = null;
                inHunk = false;
                continue;
            }

            // Hunk headers take precedence; added content is parsed before file headers so an
            // added line can itself start with "+++ b/".
            if (currentFile is not null && line.Length >= 3 && line[0] == '@' && line[1] == '@' && line[2] == ' '
                && TryParseHunkNewStart(line, out var hunkStart))
            {
                FlushRun();
                newLine = hunkStart;
                inHunk = true;
                continue;
            }

            if (currentFile is not null && inHunk && line.Length > 0)
            {
                switch (line[0])
                {
                    case '+':
                        if (runEnd < runStart)
                        {
                            runStart = newLine; // open a new run
                        }

                        runEnd = newLine;
                        newLine++;
                        continue;
                    case ' ':
                        FlushRun();
                        newLine++;
                        continue;
                    case '-':
                        FlushRun();
                        continue;
                    case '\\': // "\ No newline at end of file"
                        continue;
                    default:
                        FlushRun();
                        inHunk = false;
                        continue;
                }
            }

            FlushRun();
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = line[6..].ToString();
                sawContent = true;
                if (!index._ChangedLines.ContainsKey(currentFile))
                {
                    index._ChangedLines[currentFile] = [];
                }

                inHunk = false;
            }
            else if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                currentFile = null; // deleted file
                inHunk = false;
            }
            else if (sectionFile is not null && line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                sectionBinary = true;
                sectionFile = BinaryNewPath(line);
            }
            else if (sectionFile is not null && line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                sectionRename = true;
                sectionFile = line["rename to ".Length..].ToString();
            }
        }

        FlushRun();
        FlushSection();
        return index;
    }

    /// <summary>Parses "@@ -old[,n] +newStart[,n] @@" and yields the added-side start.</summary>
    internal static bool TryParseHunkNewStart(ReadOnlySpan<char> line, out int newStart)
    {
        newStart = 0;
        var plus = line.IndexOf('+');
        if (plus < 0)
        {
            return false;
        }

        var rest = line[(plus + 1)..];
        var digits = 0;
        var value = 0;
        while (digits < rest.Length && rest[digits] is >= '0' and <= '9')
        {
            value = value * 10 + (rest[digits] - '0');
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        // Optional ",count" then the closing " @@".
        var tail = rest[digits..];
        if (tail.StartsWith(','))
        {
            if (tail.IndexOf(" @@", StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }
        else if (!tail.StartsWith(" @@", StringComparison.Ordinal))
        {
            return false;
        }

        newStart = value;
        return true;
    }

    /// <summary>Extracts the destination path from "diff --git a/OLD b/NEW", or null.</summary>
    internal static string? DiffGitNewPath(ReadOnlySpan<char> line)
    {
        var rest = line["diff --git a/".Length..];
        var bIdx = rest.IndexOf(" b/", StringComparison.Ordinal);
        return bIdx < 0 ? null : rest[(bIdx + 3)..].ToString();
    }

    /// <summary>Extracts the destination path from "Binary files a/OLD and b/NEW differ", or null.</summary>
    internal static string? BinaryNewPath(ReadOnlySpan<char> line)
    {
        var rest = line["Binary files ".Length..];
        var andIdx = rest.IndexOf(" and b/", StringComparison.Ordinal);
        if (andIdx < 0)
        {
            return null;
        }

        var tail = rest[(andIdx + " and b/".Length)..];
        var differIdx = tail.LastIndexOf(" differ", StringComparison.Ordinal);
        return (differIdx >= 0 ? tail[..differIdx] : tail).ToString();
    }

    /// <summary>Number of coalesced ranges for a file (test seam — asserts coalescing).</summary>
    internal int RangeCount(string filePath)
        => _ChangedLines.TryGetValue(NormalizePath(filePath), out var ranges) ? ranges.Count : 0;

    /// <summary>True when the line is part of the PR's changed (added-side) lines.</summary>
    public bool Contains(string filePath, int line)
    {
        var normalized = NormalizePath(filePath);
        if (!_ChangedLines.TryGetValue(normalized, out var ranges))
        {
            return false;
        }

        // Binary search over sorted, non-overlapping ranges.
        var lo = 0;
        var hi = ranges.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var r = ranges[mid];
            if (line < r.Start)
            {
                hi = mid - 1;
            }
            else if (line > r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePath(string filePath)
        => filePath.Replace('\\', '/').TrimStart('/');
}
