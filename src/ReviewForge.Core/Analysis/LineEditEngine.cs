namespace ReviewForge.Core.Analysis;

/// <summary>One hash-anchored line edit. <see cref="FromHash"/> identifies the line to
/// replace (unique in the file, or disambiguated by <see cref="FromLine"/>); when
/// <see cref="ToHash"/> is set and differs, the range from the from-line through the
/// to-hash line is replaced. Either hash pair or the explicit line range must be given.</summary>
public sealed record LineEdit(
    string? FromHash,      // required unless FromLine+ToLine provided
    string? ToHash,        // defaults to FromHash (single-line replace)
    int? FromLine,         // disambiguator when the hash is not unique
    int? ToLine,
    string Replacement);   // "" deletes; may contain more/fewer lines than the range

/// <summary>Where one edit landed: the original (input-file) 1-based inclusive range and
/// how many replacement lines it produced (0 = deleted).</summary>
public sealed record EditOutcome(int FromLine, int ToLine, int ReplacementLineCount);

/// <summary>Result of applying a batch of line edits. On failure <see cref="Success"/> is
/// false, <see cref="Error"/> names the first problem, and the input is untouched.</summary>
public sealed record EditResult(
    bool Success,
    string? Error,
    IReadOnlyList<EditOutcome> Outcomes,
    string? NewFileHash);

/// <summary>
/// Pure engine that resolves hash-anchored edits against an in-memory line list and
/// applies them bottom-up. Atomic: any failure returns Success=false and the input is
/// untouched. Overlapping ranges are rejected after resolution. No IO.
/// </summary>
public static class LineEditEngine
{
    /// <summary>Applies edits to an in-memory line list. Atomic: any failure returns
    /// Success=false and the input is untouched. Edits are applied bottom-up;
    /// overlapping ranges are rejected. Pure; no IO.</summary>
    public static EditResult Apply(IReadOnlyList<string> lines, IReadOnlyList<LineEdit> edits, out string[] newLines)
        => Apply(lines, edits, path: null, out newLines);

    /// <summary>As <see cref="Apply"/>, formatting error messages with the file path when given.</summary>
    internal static EditResult Apply(
        IReadOnlyList<string> lines, IReadOnlyList<LineEdit> edits, string? path, out string[] newLines)
    {
        newLines = [];
        if (edits.Count == 0)
        {
            return new EditResult(true, null, [], NewFileHash(lines));
        }

        // Pass 1: resolve every edit against the ORIGINAL lines. An unresolvable or
        // overlapping batch fails before anything is applied.
        var resolved = new (int From, int To, string[] Replacement)[edits.Count];
        var outcomes = new EditOutcome[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            var replacement = SplitReplacement(edit.Replacement);
            if (!TryResolve(lines, edit, path, out var from, out var to, out var error))
            {
                return new EditResult(false, error, [], null);
            }

            resolved[i] = (from, to, replacement);
            outcomes[i] = new EditOutcome(from + 1, to + 1, replacement.Length);
        }

        // Pass 2: reject overlaps (ranges are inclusive, 0-based here).
        if (FirstOverlap(resolved) is { } overlapLine)
        {
            return new EditResult(
                false,
                $"edits overlap at line {overlapLine} — merge or reorder",
                [],
                null);
        }

        // Pass 3: apply bottom-up (descending start line) so earlier indices stay valid.
        // Prepending each segment keeps the final list in original order.
        var result = new List<string>(lines.Count);
        var cursor = lines.Count; // unprocessed tail is [cursor, lines.Count)
        foreach (var edit in resolved
            .Select((r, i) => (r.From, r.To, r.Replacement, i))
            .OrderByDescending(r => r.From))
        {
            result.InsertRange(0, lines.Skip(edit.To + 1).Take(cursor - edit.To - 1));
            result.InsertRange(0, edit.Replacement);
            cursor = edit.From;
        }

        result.InsertRange(0, lines.Take(cursor));

        newLines = [.. result];
        return new EditResult(true, null, outcomes, NewFileHash(newLines));
    }

    private static bool TryResolve(
        IReadOnlyList<string> lines,
        LineEdit edit,
        string? path,
        out int from,
        out int to,
        out string? error)
    {
        from = 0;
        to = 0;
        error = null;

        if (edit.FromHash is { } fromHash)
        {
            var matches = new List<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (string.Equals(HashLine.Of(lines[i]), fromHash, StringComparison.Ordinal))
                {
                    matches.Add(i);
                }
            }

            if (matches.Count == 0)
            {
                error = path is null
                    ? $"hash {fromHash} not present — the file changed since your last read; re-read and retry"
                    : $"hash {fromHash} not present in {path} — the file changed since your last read; re-read and retry";
                return false;
            }

            if (matches.Count > 1)
            {
                if (edit.FromLine is not { } hint)
                {
                    error = $"hash {fromHash} matches lines {string.Join(", ", matches.Select(m => m + 1))} — add fromLine/toLine to disambiguate";
                    return false;
                }

                if (!matches.Contains(hint - 1))
                {
                    error = $"hash {fromHash} not at line {hint} (found at {matches[0] + 1})";
                    return false;
                }

                from = hint - 1;
            }
            else
            {
                from = matches[0];
            }

            if (edit.ToHash is { } toHash && !string.Equals(toHash, fromHash, StringComparison.Ordinal))
            {
                var toMatches = new List<int>();
                for (var i = from + 1; i < lines.Count; i++)
                {
                    if (string.Equals(HashLine.Of(lines[i]), toHash, StringComparison.Ordinal))
                    {
                        toMatches.Add(i);
                    }
                }

                if (toMatches.Count != 1)
                {
                    error = toMatches.Count == 0
                        ? $"hash {toHash} not present in {(path ?? "file")} — the file changed since your last read; re-read and retry"
                        : $"hash {toHash} matches lines {string.Join(", ", toMatches.Select(m => m + 1))} — add fromLine/toLine to disambiguate";
                    return false;
                }

                to = toMatches[0];
            }
            else
            {
                to = from;
            }

            return ValidateRange(lines.Count, from, to, out error);
        }

        if (edit.FromLine is not { } fromLine || edit.ToLine is not { } toLine)
        {
            error = "edit needs fromHash or an explicit fromLine/toLine range";
            return false;
        }

        from = fromLine - 1;
        to = toLine - 1;
        return ValidateRange(lines.Count, from, to, out error);
    }

    private static bool ValidateRange(int lineCount, int from, int to, out string? error)
    {
        error = null;
        if (from < 0 || to >= lineCount || from > to)
        {
            error = $"invalid line range {from + 1}-{to + 1}";
            return false;
        }

        return true;
    }

    /// <summary>1-based start line of the first overlapping pair, or null.</summary>
    private static int? FirstOverlap((int From, int To, string[] Replacement)[] resolved)
    {
        var byStart = resolved
            .Select((r, i) => (r.From, r.To, i))
            .OrderBy(r => r.From)
            .ToArray();
        for (var i = 1; i < byStart.Length; i++)
        {
            if (byStart[i].From <= byStart[i - 1].To)
            {
                return byStart[i].From + 1;
            }
        }

        return null;
    }

    /// <summary>"" deletes; a trailing newline collapses so "a\n" is one line, not two.</summary>
    internal static string[] SplitReplacement(string replacement)
    {
        if (replacement.Length == 0)
        {
            return [];
        }

        var parts = replacement.Replace("\r\n", "\n").Split('\n');
        if (parts.Length > 1 && parts[^1].Length == 0)
        {
            parts = parts[..^1];
        }

        return parts;
    }

    private static string NewFileHash(IReadOnlyList<string> lines)
        => HashLine.Of(string.Join('\n', lines));
}
