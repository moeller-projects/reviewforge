using System.Text.RegularExpressions;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Parsed unified diff: which lines of which files are part of the change. Built once in
/// repository preparation, consumed by anchor validation.
/// </summary>
public sealed class DiffIndex
{
    // @@ -oldStart,oldCount +newStart,newCount @@
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    private readonly Dictionary<string, List<(int Start, int End)>> _ChangedLines = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Files => _ChangedLines.Keys;

    public static DiffIndex Parse(string unifiedDiff)
    {
        var index = new DiffIndex();
        string? currentFile = null;
        var newLine = 0;
        var inHunk = false;

        foreach (var rawLine in unifiedDiff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // Hunk headers take precedence; added content is parsed before file headers so an
            // added line can itself start with "+++ b/".
            if (currentFile is not null && HunkHeader.Match(line) is {Success: true} match)
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
                    default:
                        inHunk = false;
                        break;
                }
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                currentFile = line[6..];
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
        }

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