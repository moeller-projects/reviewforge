using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Re-anchors findings against the current checkout: the model's line numbers are hints.
/// When the finding's snippet is found in the file, the anchor is corrected to the
/// snippet's actual location; when the snippet is gone, the anchor is unverifiable.
/// </summary>
public static class AnchorResolver
{
    /// <summary>
    /// Normalized needle shorter than this is trivially matchable anywhere in the file
    /// ("}", ");", "x)") and verifies nothing — the anchor is trusted as given instead.
    /// </summary>
    public const int MinSnippetChars = 3;

    public enum Resolution
    {
        Verified,
        Reanchored,
        Unverifiable
    }

    /// <summary>
    /// File lines with the whitespace/casing normalization applied exactly once.
    /// Cache one per file (per stage run) and reuse across findings.
    /// </summary>
    public sealed class PreparedFile
    {
        public PreparedFile(string[] rawLines)
        {
            Raw = rawLines;
            Normalized = new string[rawLines.Length];
            for (var i = 0; i < rawLines.Length; i++)
            {
                Normalized[i] = DedupeKey.NormalizeSnippet(rawLines[i]);
            }
        }

        public string[] Raw { get; }
        public string[] Normalized { get; }
    }

    /// <summary>Resolves a finding's anchor against file content (lines, 0-based array).</summary>
    public static (Resolution Result, FindingAnchor? Anchor) Resolve(RichFinding finding, string[] fileLines)
        => Resolve(finding, new PreparedFile(fileLines));

    /// <summary>
    /// Resolves a finding's anchor against a pre-normalized file. The scan loop runs
    /// ordinal <see cref="string.Contains(string, StringComparison)"/> only — no regex,
    /// no per-position normalization, no allocations.
    /// </summary>
    public static (Resolution Result, FindingAnchor? Anchor) Resolve(RichFinding finding, PreparedFile file)
    {
        if (finding.Anchor is null)
        {
            return (Resolution.Verified, null); // PR-level finding, nothing to resolve
        }

        if (string.IsNullOrWhiteSpace(finding.Snippet))
        {
            // No snippet to verify against: trust the anchor as given.
            return (Resolution.Verified, finding.Anchor);
        }

        var needleLines = finding.Snippet!
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(DedupeKey.NormalizeSnippet)
            .ToArray();

        // Trivial snippet ("}", ");", …) — matches anywhere, proves nothing.
        if (needleLines.Sum(l => l.Length) < MinSnippetChars)
        {
            return (Resolution.Verified, finding.Anchor);
        }

        var norm = file.Normalized;
        var firstNeedle = needleLines[0];
        var bestStart = -1;
        var bestDistance = int.MaxValue;

        for (var start = 0; start <= norm.Length - needleLines.Length; start++)
        {
            // First-line prefilter: one ordinal substring search rejects nearly all positions.
            if (!norm[start].Contains(firstNeedle, StringComparison.Ordinal))
            {
                continue;
            }

            var matches = true;
            for (var offset = 1; offset < needleLines.Length; offset++)
            {
                if (!norm[start + offset].Contains(needleLines[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                var distance = Math.Abs(start + 1 - finding.Anchor.StartLine);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStart = start;
                    if (distance == 0)
                    {
                        break; // cannot do better than the stated line
                    }
                }
            }
        }

        if (bestStart < 0)
        {
            return (Resolution.Unverifiable, finding.Anchor);
        }

        var startLine = bestStart + 1;
        var endLine = startLine + needleLines.Length - 1;
        return startLine == finding.Anchor.StartLine && endLine == finding.Anchor.EndLine
            ? (Resolution.Verified, finding.Anchor)
            : (Resolution.Reanchored, finding.Anchor with { StartLine = startLine, EndLine = endLine });
    }
}
