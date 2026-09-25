using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Analysis;

/// <summary>
/// Turns suspicious tokens on added diff lines into anchored security findings.
/// Three detector rules, in priority order: confusable keyword (skeleton matches a
/// whitelisted ASCII keyword — high), mixed-script identifier (scripts mix with a
/// disallowed script — medium), whole-token lookalike (single non-Latin script whose
/// skeleton is entirely ASCII but differs from the token — medium, P2-30).
/// </summary>
public static class HomoglyphDiffAnalyzer
{
    public static IReadOnlyList<RichFinding> Analyze(string diff, HomoglyphDetector.Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var findings = new List<RichFinding>();
        var opts = options ?? HomoglyphDetector.Options.Default;
        string? file = null;
        var newLine = 0;
        var inHunk = false;

        // EnumerateLines avoids the Split('\n') array + per-line string (P2-27); content lines
        // are materialized only when they need Preprocess/ScanLine.
        foreach (var raw in diff.AsSpan().EnumerateLines())
        {
            var line = raw.TrimEnd('\r');

            // Ordering mirrors DiffIndex: hunk headers first, then in-hunk content, then the
            // +++ file header — so an added line that itself starts with "+++" is scanned as
            // content, never mistaken for a header.
            if (file is not null && line.StartsWith("@@", StringComparison.Ordinal) && TryReadNewLine(line, out newLine))
            {
                inHunk = true;
                continue;
            }

            if (inHunk && file is not null && line.Length > 0)
            {
                var marker = line[0];
                if (marker is not ('+' or ' ' or '-' or '\\'))
                {
                    inHunk = false;
                }
                else
                {
                    if (marker == '+')
                    {
                        var content = line[1..];
                        // Pure-ASCII added lines can never flag, and Preprocess (NFKC +
                        // sanitizer) cannot create non-ASCII from ASCII — skip both.
                        if (content.IndexOfAnyExceptInRange((char)0x00, (char)0x7F) >= 0)
                        {
                            var processed = PipelineText.Preprocess(content.ToString());
                            foreach (var token in HomoglyphDetector.ScanLine(processed, newLine, opts))
                            {
                                var ruleId = token.Reason switch
                                {
                                    "confusable keyword" => "homoglyph/confusable-keyword",
                                    "mixed-script identifier" => "homoglyph/mixed-script-identifier",
                                    _ => "homoglyph/whole-token-lookalike",
                                };
                                findings.Add(new RichFinding
                                {
                                    RuleId = ruleId,
                                    Title = token.Reason,
                                    Severity = token.Reason == "confusable keyword" ? "high" : "medium",
                                    Category = "security",
                                    Description = $"Token '{token.Token}' uses characters that can be confused with '{token.AsciiLookalike}'.",
                                    Snippet = content.ToString(),
                                    Suggestion = token.AsciiLookalike is null ? null : $"Use '{token.AsciiLookalike}'.",
                                    Anchor = new FindingAnchor(file, newLine, newLine)
                                });
                            }
                        }

                        newLine++;
                    }
                    else if (marker == ' ')
                    {
                        newLine++;
                    }

                    continue;
                }
            }

            if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                file = null; // deleted file
                inHunk = false;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (DiffPathParser.TryDecodeToken(line[4..], out var decoded) && DiffPathParser.TryStripBPrefix(decoded, out var rel))
                {
                    file = rel;
                }

                inHunk = false;
            }
        }

        return findings;
    }

    private static bool TryReadNewLine(ReadOnlySpan<char> line, out int newLine)
    {
        newLine = 0;
        var plus = line.IndexOf('+');
        if (plus < 0)
            return false;
        var start = plus + 1;
        var end = start;
        while (end < line.Length && char.IsDigit(line[end]))
            end++;
        return end > start && int.TryParse(line[start..end], out newLine);
    }
}
