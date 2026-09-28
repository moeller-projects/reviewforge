namespace ReviewForge.Core.Analysis;

/// <summary>Scans text lines for identifier references using ordinal index searches and boundaries.</summary>
public static class ReferenceScanner
{
    /// <summary>Returns whether an identifier is syntactically suitable for a reference scan.</summary>
    public static bool IsValidIdentifier(string identifier)
    {
        if (identifier is null || identifier.Length is < 2 or > 128)
        {
            return false;
        }

        foreach (var segment in identifier.Split('.'))
        {
            if (segment.Length == 0
                || (!char.IsLetter(segment[0]) && segment[0] != '_')
                || !segment.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
    /// <summary>Yields (1-based line number, line) where the identifier occurs on a word boundary.</summary>
    public static IEnumerable<(int LineNo, string Line)> ScanLines(
        IEnumerable<string> lines, string identifier, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            var offset = 0;
            while (offset <= line.Length - identifier.Length)
            {
                var found = line.IndexOf(identifier, offset, comparison);
                if (found < 0)
                {
                    break;
                }

                var before = found == 0 ? '\0' : line[found - 1];
                var afterIndex = found + identifier.Length;
                var after = afterIndex == line.Length ? '\0' : line[afterIndex];
                if (!IsIdentifierPart(before) && !IsIdentifierPart(after))
                {
                    yield return (lineNo, line);
                    break;
                }

                offset = found + Math.Max(identifier.Length, 1);
            }
        }
    }

    private static bool IsIdentifierPart(char c)
        => char.IsLetterOrDigit(c) || c == '_';
}
