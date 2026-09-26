namespace ReviewForge.Core.AutoFix.Fixers;

/// <summary>
/// Fixes bash.unquoted-vars: wraps unquoted <c>$NAME</c> / <c>${NAME}</c> variable
/// expansions on the anchor line in double quotes, preserving the original indentation.
/// Char-scans the line tracking single/double quote state (backslash escapes honored).
/// Special variables ($$, $?, $1, $@, $#, $!, $-, etc.), command substitutions, and
/// anything inside quotes are left alone. Fail-closed on here-doc markers and
/// unbalanced quote state.
/// </summary>
public sealed class BashUnquotedVarsFixer : IFindingFixer
{
    public string RuleId => "bash.unquoted-vars";

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
        if (ContainsHereDocMarker(line)
            || EndsWithUnescapedBackslash(line)
            || line.Contains("$(", StringComparison.Ordinal)
            || line.Contains('`')
            || ContainsUnsafeForWordListExpansion(line)
            || ContainsUnsafeConditionalExpansion(line))
        {
            return null;
        }

        var rewritten = Rewrite(line, out var changed);
        if (!changed || rewritten is null)
        {
            return null;
        }

        return new FixProposal(
            context.FilePath,
            anchor.StartLine,
            anchor.StartLine,
            rewritten,
            "wraps the unquoted variable expansion in double quotes to prevent word splitting and globbing");
    }

    private static bool EndsWithUnescapedBackslash(string line)
    {
        var backslashes = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }

    private static bool ContainsUnsafeForWordListExpansion(string line)
    {
        if (!line.TrimStart().StartsWith("for ", StringComparison.Ordinal))
        {
            return false;
        }

        var afterFor = line.TrimStart()[4..];
        var variableEnd = 0;
        while (variableEnd < afterFor.Length
               && (char.IsLetterOrDigit(afterFor[variableEnd]) || afterFor[variableEnd] == '_'))
        {
            variableEnd++;
        }

        if (variableEnd == 0 || !afterFor[variableEnd..].TrimStart().StartsWith("in", StringComparison.Ordinal))
        {
            return false;
        }

        var inOffset = line.IndexOf("in", line.IndexOf("for", StringComparison.Ordinal) + 3, StringComparison.Ordinal);
        if (inOffset < 0)
        {
            return false;
        }

        var listEnd = line.IndexOf("; do", inOffset + 2, StringComparison.Ordinal);
        if (listEnd < 0)
        {
            listEnd = line.Length;
        }

        return HasUnquotedExpansion(line, inOffset + 2, listEnd);
    }

    private static bool ContainsUnsafeConditionalExpansion(string line)
    {
        var open = line.IndexOf("[[", StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        var close = line.IndexOf("]]", open + 2, StringComparison.Ordinal);
        if (close < 0)
        {
            return true;
        }

        var expression = line[(open + 2)..close];
        if (!expression.Contains("==", StringComparison.Ordinal)
            && !expression.Contains("=~", StringComparison.Ordinal))
        {
            return false;
        }

        return HasUnquotedExpansion(line, open + 2, close);
    }

    private static bool HasUnquotedExpansion(string line, int start, int end)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = start; i < end; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < end)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '$' && !inSingle && !inDouble && ReadExpansion(line, i) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsHereDocMarker(string line)
    {
        // `<<EOF` / `<<-EOF` / `<<<` — quoting expansions around here-docs changes semantics.
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '<' && (line[i + 1] == '<' || line[i + 1] == '('))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Null when quote state ends unbalanced or a substitution contains quotes.</summary>
    private static string? Rewrite(string line, out bool changed)
    {
        changed = false;
        var sb = new System.Text.StringBuilder(line.Length + 8);
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && !inSingle && i + 1 < line.Length)
            {
                sb.Append(c).Append(line[i + 1]);
                i++;
                continue;
            }

            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                sb.Append(c);
                continue;
            }

            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                sb.Append(c);
                continue;
            }

            if (c == '$' && !inSingle && !inDouble)
            {
                var expansion = ReadExpansion(line, i);
                if (expansion is null)
                {
                    sb.Append(c);
                    continue;
                }

                var end = expansion.Value.End;
                sb.Append('"').Append(line, i, end - i).Append('"');
                changed = true;
                i = end - 1;
                continue;
            }

            sb.Append(c);
        }

        if (inSingle || inDouble)
        {
            return null;
        }

        return sb.ToString();
    }

    /// <summary>Reads a $NAME / ${NAME} expansion starting at index i; null for special
    /// variables and non-identifiers, which must never be quoted.</summary>
    private static (string Expansion, int End)? ReadExpansion(string line, int i)
    {
        var start = i;
        if (i + 1 >= line.Length)
        {
            return null;
        }

        if (line[i + 1] == '{')
        {
            var close = line.IndexOf('}', i + 2);
            if (close < 0)
            {
                return null;
            }

            var name = line[(i + 2)..close];
            if (name.Length == 0 || !name.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return null;
            }

            return (line[start..(close + 1)], close + 1);
        }

        var j = i + 1;
        if (!char.IsLetter(line[j]) && line[j] != '_')
        {
            return null; // $$, $?, $1, $@, $(, $', $" ... — special forms stay untouched
        }

        while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_'))
        {
            j++;
        }

        return (line[start..j], j);
    }
}
