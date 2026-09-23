using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Analysis;

/// <summary>Turns suspicious tokens on added diff lines into anchored security findings.</summary>
public static class HomoglyphDiffAnalyzer
{
    public static IReadOnlyList<RichFinding> Analyze(string diff, HomoglyphDetector.Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var findings = new List<RichFinding>();
        string? file = null;
        var newLine = 0;
        var inHunk = false;

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                file = line[6..];
                inHunk = false;
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal) && TryReadNewLine(line, out newLine))
            {
                inHunk = true;
                continue;
            }

            if (!inHunk || file is null || line.Length == 0)
                continue;

            if (line[0] == '+')
            {
                var content = PipelineText.Preprocess(line[1..]);
                foreach (var token in HomoglyphDetector.ScanLine(content, newLine, options))
                {
                    var ruleId = token.Reason == "confusable keyword"
                        ? "homoglyph/confusable-keyword"
                        : "homoglyph/mixed-script-identifier";
                    findings.Add(new RichFinding
                    {
                        RuleId = ruleId,
                        Title = token.Reason,
                        Severity = token.Reason == "confusable keyword" ? "high" : "medium",
                        Category = "security",
                        Description = $"Token '{token.Token}' uses characters that can be confused with '{token.AsciiLookalike}'.",
                        Snippet = line[1..],
                        Suggestion = token.AsciiLookalike is null ? null : $"Use '{token.AsciiLookalike}'.",
                        Anchor = new FindingAnchor(file, newLine, newLine)
                    });
                }

                newLine++;
            }
            else if (line[0] is ' ' or '-')
            {
                if (line[0] == ' ')
                    newLine++;
            }
        }

        return findings;
    }

    private static bool TryReadNewLine(string line, out int newLine)
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
