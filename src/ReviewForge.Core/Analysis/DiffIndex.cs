using System.Text.RegularExpressions;

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
/// repository preparation, consumed by anchor validation. Non-text sections (binary,
/// mode-only, content-free rename) are classified so the scope guard can exclude them.
/// </summary>
public sealed class DiffIndex
{
    // @@ -oldStart,oldCount +newStart,newCount @@
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);
    private static readonly Regex DiffGitHeader = new(@"^diff --git a/.+ b/(?<new>.+)$", RegexOptions.Compiled);
    private static readonly Regex BinaryMarker = new(@"^Binary files .+ and b/(?<new>.+) differ$", RegexOptions.Compiled);
    private static readonly Regex RenameToMarker = new(@"^rename to (?<new>.+)$", RegexOptions.Compiled);

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

        foreach (var rawLine in unifiedDiff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // A "diff --git" header is a hard section boundary: it must be recognized even
            // when the previous file's hunk is still open (real git emits it right after hunk content).
            if (DiffGitHeader.Match(line) is {Success: true} header)
            {
                FlushSection();
                sectionFile = header.Groups["new"].Value;
                sawContent = false;
                sectionBinary = false;
                sectionRename = false;
                currentFile = null;
                inHunk = false;
            }
            // Hunk headers take precedence; added content is parsed before file headers so an
            // added line can itself start with "+++ b/".
            else if (currentFile is not null && HunkHeader.Match(line) is {Success: true} match)
            {
                newLine = int.Parse(match.Groups[1].Value);
                inHunk = true;
            }
            else if (currentFile is not null && inHunk && line.Length > 0)
            {
                switch (line[0])
                {
                    case '+':
                        index._ChangedLines[currentFile].Add((newLine, newLine));
                        newLine++;
                        break;
                    case ' ':
                        newLine++;
                        break;
                    case '-':
                        break;
                    case '\\': // "\ No newline at end of file"
                        break;
                    default:
                        inHunk = false;
                        break;
                }
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = line[6..];
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
            else if (sectionFile is not null && BinaryMarker.Match(line) is {Success: true} binary)
            {
                sectionBinary = true;
                sectionFile = binary.Groups["new"].Value; // authoritative destination name
            }
            else if (sectionFile is not null && RenameToMarker.Match(line) is {Success: true} rename)
            {
                sectionRename = true;
                sectionFile = rename.Groups["new"].Value;
            }
        }

        FlushSection();
        return index;
    }

    /// <summary>True when the line is part of the PR's changed (added-side) lines.</summary>
    public bool Contains(string filePath, int line)
    {
        var normalized = NormalizePath(filePath);
        return _ChangedLines.TryGetValue(normalized, out var ranges)
               && ranges.Exists(r => line >= r.Start && line <= r.End);
    }

    private static string NormalizePath(string filePath)
        => filePath.Replace('\\', '/').TrimStart('/');
}
