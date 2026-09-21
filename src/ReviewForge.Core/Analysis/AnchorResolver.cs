using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Re-anchors findings against the current checkout: the model's line numbers are hints.
/// When the finding's snippet is found in the file, the anchor is corrected to the
/// snippet's actual location; when the snippet is gone, the anchor is unverifiable.
/// </summary>
public static class AnchorResolver
{
    public enum Resolution
    {
        Verified,
        Reanchored,
        Unverifiable
    }

    /// <summary>
    /// Resolves a finding's anchor against file content (lines, 0-based array).
    /// Returns the resolution and the effective anchor (corrected when reanchored).
    /// </summary>
    public static (Resolution Result, FindingAnchor? Anchor) Resolve(RichFinding finding, string[] fileLines)
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

        for (var start = 0; start <= fileLines.Length - needleLines.Length; start++)
        {
            var matches = true;
            for (var offset = 0; offset < needleLines.Length; offset++)
            {
                if (!DedupeKey.NormalizeSnippet(fileLines[start + offset])
                        .Contains(needleLines[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                var startLine = start + 1;
                var endLine = startLine + needleLines.Length - 1;
                return startLine == finding.Anchor.StartLine && endLine == finding.Anchor.EndLine
                    ? (Resolution.Verified, finding.Anchor)
                    : (Resolution.Reanchored, finding.Anchor with {StartLine = startLine, EndLine = endLine});
            }
        }

        return (Resolution.Unverifiable, finding.Anchor);
    }
}