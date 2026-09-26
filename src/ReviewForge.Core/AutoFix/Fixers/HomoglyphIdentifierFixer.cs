using ReviewForge.Core.Analysis;

namespace ReviewForge.Core.AutoFix.Fixers;

/// <summary>
/// Fixes homoglyph identifiers on the anchor line by replacing the single suspicious
/// token with its ASCII lookalike. Registered per rule id (one instance per homoglyph
/// rule). Fail-closed: anything but exactly one uniquely-occurring token with a known
/// lookalike declines.
/// </summary>
public sealed class HomoglyphIdentifierFixer(string ruleId) : IFindingFixer
{
    public string RuleId { get; } = ruleId;

    public FixProposal? TryPropose(FixContext context)
    {
        var anchor = context.Finding.Anchor;
        if (anchor is null || anchor.StartLine != anchor.EndLine)
        {
            return null; // multi-line findings are out of scope for this fixer
        }

        var lineIndex = anchor.StartLine - 1;
        if (lineIndex < 0 || lineIndex >= context.FileLines.Length)
        {
            return null;
        }

        var content = context.FileLines[lineIndex];
        var tokens = HomoglyphDetector.ScanLine(content, anchor.StartLine);
        if (tokens.Count != 1)
        {
            return null;
        }

        var token = tokens[0];
        if (token.AsciiLookalike is not { } lookalike)
        {
            return null;
        }

        // The token must occur exactly once on the line, else we cannot pick a unique
        // replacement occurrence.
        var first = content.IndexOf(token.Token, StringComparison.Ordinal);
        if (first < 0 || content.IndexOf(token.Token, first + token.Token.Length, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        var fixedLine = content[..first] + lookalike + content[(first + token.Token.Length)..];
        return new FixProposal(
            context.FilePath,
            anchor.StartLine,
            anchor.StartLine,
            fixedLine,
            $"replaces the confusable identifier '{token.Token}' with its ASCII equivalent '{lookalike}'");
    }
}
