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
        var mutableCount = 0;
        foreach (var p in parameters)
        {
            var trimmed = p.Trim();
            if (TryParseMutableDefault(trimmed, out var name, out var original))
            {
                mutableCount++;
                mutableName = name;
                mutableOriginal = original;
            }
            else if (trimmed.Contains('=') && LooksMutable(trimmed))
            {
                return null; // a mutable default we cannot derive safely (list(), dict(), …)
            }
        }

        if (mutableCount != 1 || mutableName is null)
        {
            return null;
        }

        var bodyIndent = FindBodyIndent(context.FileLines, lineIndex + 1);
        if (bodyIndent is null)
        {
            return null;
        }

        var newDef = MutableDefault().Replace(line, "= None");
        var guard = $"{bodyIndent}if {mutableName} is None: {mutableName} = {mutableOriginal}";
        return new FixProposal(
            context.FilePath,
            anchor.StartLine,
            anchor.StartLine,
            newDef + "\n" + guard,
            $"replaces the mutable default {mutableOriginal} with None and guards with an `if {mutableName} is None` body line");
    }

    [GeneratedRegex(@"=\s*(\[\s*\]|\{\s*\})")]
    private static partial Regex MutableDefault();

    private static string? FindBodyIndent(string[] lines, int afterIndex)
    {
        for (var i = afterIndex; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Trim().Length == 0)
            {
                continue;
            }

            var indentLength = l.Length - l.TrimStart().Length;
            return l[..indentLength];
        }

        return null;
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
