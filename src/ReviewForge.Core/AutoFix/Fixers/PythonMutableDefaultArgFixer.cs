using System.Text.RegularExpressions;

namespace ReviewForge.Core.AutoFix.Fixers;

/// <summary>
/// Fixes py.mutable-default-arg: a single-line <c>def …(… name=[] …):</c> (or
/// <c>={}</c>) becomes <c>… name=None …:</c> plus a guard line
/// <c>if name is None: name = []</c> indented to the body. Fail-closed: multi-line
/// signatures, more than one mutable default, other mutable forms (list()/dict()),
/// or an undeterminable body indent decline.
/// </summary>
public sealed partial class PythonMutableDefaultArgFixer : IFindingFixer
{
    public string RuleId => "py.mutable-default-arg";

    public FixProposal? TryPropose(FixContext context)
    {
        var anchor = context.Finding.Anchor;
        if (anchor is null || anchor.StartLine != anchor.EndLine)
        {
            return null;
        }

        var lineIndex = anchor.StartLine - 1;
        if (lineIndex < 0 || lineIndex >= context.FileLines.Length)
        {
            return null;
        }

        var line = context.FileLines[lineIndex];
        var openParen = line.IndexOf('(');
        var closeParen = line.LastIndexOf(')');
        if (openParen < 0 || closeParen < openParen || !line[(closeParen + 1)..].TrimStart().StartsWith(':'))
        {
            return null; // multi-line signature or not a def line ending with ':'
        }

        var beforeParen = line[..openParen].TrimStart();
        if (!beforeParen.StartsWith("def ", StringComparison.Ordinal))
        {
            return null;
        }

        var paramsText = line[(openParen + 1)..closeParen];
        var parameters = SplitTopLevel(paramsText);
        string? mutableName = null;
        var mutableOriginal = string.Empty;
        var mutableDefaultStart = -1;
        var mutableDefaultLength = 0;
        var mutableCount = 0;
        var parameterSearchStart = 0;
        foreach (var p in parameters)
        {
            var parameterOffset = paramsText.IndexOf(p, parameterSearchStart, StringComparison.Ordinal);
            if (parameterOffset < 0)
            {
                return null;
            }

            parameterSearchStart = parameterOffset + p.Length;
            var trimmed = p.Trim();
            if (TryParseMutableDefault(trimmed, out var name, out var original))
            {
                mutableCount++;
                mutableName = name;
                mutableOriginal = original;
                var leadingWhitespace = p.Length - p.TrimStart().Length;
                var equals = trimmed.IndexOf('=');
                var expressionOffset = equals + 1;
                while (expressionOffset < trimmed.Length && char.IsWhiteSpace(trimmed[expressionOffset]))
                {
                    expressionOffset++;
                }

                mutableDefaultStart = openParen + 1 + parameterOffset + leadingWhitespace + expressionOffset;
                mutableDefaultLength = original.Length;
            }
            else if (trimmed.Contains('=') && LooksMutable(trimmed))
            {
                return null; // a mutable default we cannot derive safely (list(), dict(), …)
            }
        }

        if (mutableCount != 1 || mutableName is null || mutableDefaultStart < 0)
        {
            return null;
        }

        var mutableMatches = MutableDefault().Matches(line);
        if (mutableMatches.Count != 1
            || mutableMatches[0].Groups[1].Index != mutableDefaultStart
            || mutableMatches[0].Groups[1].Length != mutableDefaultLength)
        {
            return null; // another []/{} occurrence (for example in a string) is unsafe to rewrite
        }

        var definitionIndent = line.Length - line.TrimStart().Length;
        var body = FindBodyIndent(context.FileLines, lineIndex + 1, definitionIndent);
        if (body is null)
        {
            return null;
        }

        var newDef = line[..mutableDefaultStart] + "None" + line[(mutableDefaultStart + mutableDefaultLength)..];
        var guard = $"{body.Value.Indent}if {mutableName} is None: {mutableName} = {mutableOriginal}";
        var endLine = anchor.StartLine;
        var replacement = newDef;
        var firstBody = context.FileLines[body.Value.Index].TrimStart();
        var firstBodyIsDocstring = firstBody.StartsWith("\"\"\"", StringComparison.Ordinal)
            || firstBody.StartsWith("'''", StringComparison.Ordinal);
        if (firstBodyIsDocstring && !IsSingleLineDocstring(firstBody))
        {
            return null;
        }

        if (IsSingleLineDocstring(firstBody))
        {
            replacement += "\n" + string.Join("\n", context.FileLines[(lineIndex + 1)..(body.Value.Index + 1)]);
            replacement += "\n" + guard;
            endLine = body.Value.Index + 1;
        }
        else
        {
            replacement += "\n" + guard;
        }

        return new FixProposal(
            context.FilePath,
            anchor.StartLine,
            endLine,
            replacement,
            $"replaces the mutable default {mutableOriginal} with None and guards with an `if {mutableName} is None` body line");
    }

    [GeneratedRegex(@"=\s*(\[\s*\]|\{\s*\})")]
    private static partial Regex MutableDefault();

    private static (string Indent, int Index)? FindBodyIndent(string[] lines, int afterIndex, int definitionIndent)
    {
        for (var i = afterIndex; i < lines.Length; i++)
        {
            var l = lines[i];
            var trimmed = l.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var indentLength = l.Length - l.TrimStart().Length;
            if (indentLength <= definitionIndent)
            {
                return null;
            }

            return (l[..indentLength], i);
        }

        return null;
    }

    private static bool IsSingleLineDocstring(string text)
    {
        var delimiter = text.StartsWith("\"\"\"", StringComparison.Ordinal)
            ? "\"\"\""
            : text.StartsWith("'''", StringComparison.Ordinal) ? "'''" : null;
        return delimiter is not null && text.IndexOf(delimiter, delimiter.Length, StringComparison.Ordinal) >= delimiter.Length;
    }

    private static string[] SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return [.. parts];
    }

    private static bool TryParseMutableDefault(string p, out string name, out string original)
    {
        name = string.Empty;
        original = string.Empty;
        var eq = p.IndexOf('=');
        if (eq < 0)
        {
            return false;
        }

        var namePart = p[..eq].Trim();
        var defaultPart = p[(eq + 1)..].Trim();
        if (defaultPart is not ("[]" or "{}"))
        {
            return false;
        }

        if (namePart.Length == 0 || !char.IsLetter(namePart[0]) && namePart[0] != '_')
        {
            return false;
        }

        // Strip an optional `name: annotation` down to the identifier.
        var colon = namePart.IndexOf(':');
        if (colon >= 0)
        {
            namePart = namePart[..colon].Trim();
        }

        if (namePart.Length == 0 || namePart.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_'))
        {
            return false;
        }

        name = namePart;
        original = defaultPart;
        return true;
    }

    private static bool LooksMutable(string p)
    {
        var eq = p.IndexOf('=');
        if (eq < 0)
        {
            return false;
        }

        var d = p[(eq + 1)..].Trim();
        return d.Contains('[') || d.Contains('{') || d.StartsWith("list(", StringComparison.Ordinal)
            || d.StartsWith("dict(", StringComparison.Ordinal);
    }
}
