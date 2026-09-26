using System.ComponentModel;
using System.Text;
using ReviewForge.Core.Analysis;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Hash-anchored line editor for checkout files. Direct System.IO by design — same
/// exception as RepoReadTools/ValidateFindingsStage (see the IWorkspaceFs scope note).
/// All paths pass <see cref="RepoPathGuard"/> (containment + deny). Writes are
/// additionally restricted to the writable set (a single anchored file for thread-command
/// fix passes). Refusal parity with <see cref="RepoReadTools.ReadFile"/> (binary,
/// unreadable, missing). Files whose terminators are neither >90% LF nor >90% CRLF are
/// refused for editing. Writes are atomic (temp file in the same directory + move).
/// </summary>
public class HashLineEditor
{
    public const int MaxLines = RepoReadTools.DefaultMaxLines;

    private readonly RepoPathGuard _Guard;
    private readonly HashSet<string> _Writable; // RepoPath.Normalized, OrdinalIgnoreCase
    private readonly Dictionary<string, List<SessionEdit>> _Sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _RawCache = new(StringComparer.OrdinalIgnoreCase);

    public HashLineEditor(RepoPathGuard guard, IReadOnlySet<string> writableRelativePaths)
    {
        _Guard = guard;
        _Writable = new HashSet<string>(
            writableRelativePaths.Select(RepoPath.Normalize), StringComparer.OrdinalIgnoreCase);
    }

    [Description("Read a file with per-line content hashes for edit anchoring.")]
    public virtual string ReadFileWithHashes(
        [Description("File path relative to repo root")] string path,
        [Description("1-based first line to read")] int startLine = 1,
        [Description("Maximum number of lines")] int? maxLines = null)
    {
        var rel = RepoPath.Normalize(path ?? string.Empty);
        var full = _Guard.Resolve(path, out _);
        if (full is null)
        {
            return $"access denied: {path}";
        }

        var (raw, error) = ReadRawLines(rel, full);
        if (error is not null)
        {
            return error;
        }

        var start = Math.Max(1, startLine);
        var take = Math.Min(maxLines ?? MaxLines, MaxLines);
        var hashes = raw!.Select(HashLine.Of).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hashes)
        {
            if (!seen.Add(h))
            {
                duplicated.Add(h);
            }
        }

        var sb = new StringBuilder();
        var emitted = 0;
        for (var i = start - 1; i < raw.Count && emitted < take; i++, emitted++)
        {
            var marker = duplicated.Contains(hashes[i]) ? "*" : string.Empty;
            sb.Append(i + 1).Append(' ').Append(hashes[i]).Append(marker).Append(": ")
                .AppendLine(HashLine.Normalize(raw[i]));
        }

        if (emitted == 0 && raw.Count < start)
        {
            return $"file has {raw.Count} lines; startLine {start} is out of range";
        }

        if (raw.Count > start - 1 + emitted)
        {
            sb.AppendLine($"…[{raw.Count - (start - 1 + emitted)} more lines]");
        }

        return sb.ToString();
    }

    [Description("Apply one or more line edits to a file. All edits must succeed or none are applied.")]
    public virtual string EditFile(
        [Description("File path relative to repo root")] string path,
        [Description("Line edits to apply atomically")] LineEdit[] edits)
    {
        if (!TryResolveWritable(path, out var rel, out var full, out var denyError))
        {
            return denyError!;
        }

        if (edits.Length == 0)
        {
            return "no edits provided";
        }

        var (raw, readError) = ReadRawLines(rel, full!);
        if (readError is not null)
        {
            return readError;
        }

        if (!TryCheckEditable(raw!, out var endingsError, out var newLine))
        {
            return endingsError!;
        }

        var content = raw!.Select(HashLine.Normalize).ToArray();
        var result = LineEditEngine.Apply(content, edits, rel, out var newLines);
        if (!result.Success)
        {
            return result.Error!;
        }

        WriteAtomic(full!, newLines, newLine!);
        RecordSession(rel, result.Outcomes);
        _RawCache.Remove(rel);
        return FormatApplied(rel, edits.Length, result.Outcomes, result.NewFileHash!);
    }

    /// <summary>Reads a file as normalized content lines (no terminators). Pipeline-facing.</summary>
    public virtual string[] ReadAllLines(string relativePath)
    {
        var rel = RepoPath.Normalize(relativePath);
        var full = _Guard.Resolve(rel, out _);
        if (full is null)
        {
            throw new InvalidOperationException($"access denied: {relativePath}");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"not found: {rel}", full);
        }

        return File.ReadAllLines(full).Select(HashLine.Normalize).ToArray();
    }

    /// <summary>
    /// Replaces <c>[startLine..endLine]</c> (1-based inclusive) with <paramref name="replacement"/>
    /// only when the current content hash of that range equals <paramref name="expectedRangeHash"/>
    /// (drift guard for pipeline-applied fixes). Records the session edit on success.
    /// </summary>
    public virtual EditResult ApplyRange(
        string relativePath, int startLine, int endLine, string replacement, string expectedRangeHash)
    {
        if (!TryResolveWritable(relativePath, out var rel, out var full, out var denyError))
        {
            return new EditResult(false, denyError ?? $"access denied: {relativePath}", [], null);
        }

        var (raw, readError) = ReadRawLines(rel, full!);
        if (readError is not null)
        {
            return new EditResult(false, readError, [], null);
        }

        if (!TryCheckEditable(raw!, out var endingsError, out var newLine))
        {
            return new EditResult(false, endingsError, [], null);
        }

        var content = raw!.Select(HashLine.Normalize).ToArray();
        if (startLine < 1 || endLine > content.Length || startLine > endLine)
        {
            return new EditResult(false, $"invalid line range {startLine}-{endLine}", [], null);
        }

        var current = HashLine.Of(string.Join('\n', content.Skip(startLine - 1).Take(endLine - startLine + 1)));
        if (!string.Equals(current, expectedRangeHash, StringComparison.Ordinal))
        {
            return new EditResult(
                false,
                $"hash mismatch at {rel}:{startLine}-{endLine} — re-read and retry",
                [],
                null);
        }

        var edit = new LineEdit(null, null, startLine, endLine, replacement);
        var result = LineEditEngine.Apply(content, [edit], rel, out var newLines);
        if (!result.Success)
        {
            return result;
        }

        WriteAtomic(full!, newLines, newLine!);
        RecordSession(rel, result.Outcomes);
        _RawCache.Remove(rel);
        return result;
    }

    /// <summary>
    /// Writes content lines using the file's original per-line endings when the editor
    /// read the file earlier in this session and the line count matches (byte-exact
    /// revert); otherwise the dominant line ending is used. Writes outside the writable
    /// set fail.
    /// </summary>
    public virtual void WriteAllLines(string relativePath, string[] lines)
    {
        if (!TryResolveWritable(relativePath, out var rel, out var full, out var denyError))
        {
            throw new InvalidOperationException(denyError ?? $"access denied: {relativePath}");
        }

        string[]? cached = null;
        _RawCache.TryGetValue(rel, out cached);
        if (cached is not null && cached.Length == lines.Length)
        {
            // Revert path: restore each line with its original ending.
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                var raw = cached[i];
                if (raw.EndsWith("\r\n", StringComparison.Ordinal))
                {
                    sb.Append("\r\n");
                }
                else if (raw.EndsWith("\n", StringComparison.Ordinal))
                {
                    sb.Append('\n');
                }
            }

            File.WriteAllText(full!, sb.ToString());
            return;
        }

        var newLine = "\n";
        if (cached is { Length: > 0 })
        {
            newLine = HashLine.DetectNewLine(cached);
        }
        else if (File.Exists(full))
        {
            var (raw, _) = ReadRawLines(rel, full!);
            if (raw is { Count: > 0 })
            {
                newLine = HashLine.DetectNewLine(raw);
            }
        }

        File.WriteAllText(full!, string.Join(newLine, lines) + newLine);
    }

    /// <summary>The merged change this editor session produced on a file: the contiguous
    /// region from the first to the last edited line, with its CURRENT content as
    /// Replacement. Null when the session made no edits to the file.</summary>
    public virtual (int StartLine, int EndLine, string Replacement)? GetSessionChange(string relativePath)
    {
        var rel = RepoPath.Normalize(relativePath);
        if (!_Sessions.TryGetValue(rel, out var edits) || edits.Count == 0)
        {
            return null;
        }

        var start = edits.Min(e => e.OriginalStart);
        var originalEnd = edits.Max(e => e.OriginalEnd);
        var netShift = edits.Sum(e => e.ReplacementLineCount - (e.OriginalEnd - e.OriginalStart + 1));
        var currentEnd = originalEnd + netShift;

        var full = _Guard.Resolve(rel, out _);
        if (full is null || !File.Exists(full))
        {
            return null;
        }

        var current = File.ReadAllLines(full).Select(HashLine.Normalize).ToArray();
        var length = Math.Max(0, currentEnd - start + 1);
        var replacement = string.Join('\n', current.Skip(start - 1).Take(length));
        return (start, currentEnd, replacement);
    }

    private bool TryResolveWritable(string path, out string rel, out string? full, out string? error)
    {
        rel = RepoPath.Normalize(path ?? string.Empty);
        full = _Guard.Resolve(path, out error);
        if (full is null)
        {
            error = $"access denied: {path}";
            return false;
        }

        if (!_Writable.Contains(rel))
        {
            error = $"access denied: {path} is outside the writable set";
            full = null;
            return false;
        }

        if (!File.Exists(full))
        {
            error = $"not found: {path}";
            return false;
        }

        return true;
    }

    /// <summary>Editable only when one ending family holds >90% of terminated lines.</summary>
    private static bool TryCheckEditable(IReadOnlyList<string> rawLines, out string? error, out string? newLine)
    {
        error = null;
        var crlf = 0;
        var lf = 0;
        foreach (var l in rawLines)
        {
            if (l.EndsWith("\r\n", StringComparison.Ordinal))
            {
                crlf++;
            }
            else if (l.EndsWith("\n", StringComparison.Ordinal))
            {
                lf++;
            }
        }

        var terminated = crlf + lf;
        if (terminated > 0 && !(lf > terminated * 0.9) && !(crlf > terminated * 0.9))
        {
            error = "refused: mixed line endings";
            newLine = null;
            return false;
        }

        newLine = crlf > lf ? "\r\n" : "\n";
        return true;
    }

    /// <summary>Reads raw lines WITH their terminators; binary/unreadable/missing refusal
    /// parity with RepoReadTools.ReadFile. Caches the raw lines for byte-exact reverts.</summary>
    private (IReadOnlyList<string>? Lines, string? Error) ReadRawLines(string rel, string full)
    {
        if (!File.Exists(full))
        {
            return (null, $"not found: {rel}");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"unreadable: {ex.Message}");
        }

        var scanLength = Math.Min(bytes.Length, 8192);
        for (var i = 0; i < scanLength; i++)
        {
            if (bytes[i] == 0)
            {
                return (null, "refused: binary file");
            }
        }

        var text = Encoding.UTF8.GetString(bytes);
        var lines = SplitKeepEndings(text);
        _RawCache[rel] = lines;
        return (lines, null);
    }

    private static string[] SplitKeepEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return [.. lines];
    }

    private void WriteAtomic(string full, string[] lines, string newLine)
    {
        var dir = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(dir, ".rf-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, string.Join(newLine, lines) + newLine);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void RecordSession(string rel, IReadOnlyList<EditOutcome> outcomes)
    {
        if (!_Sessions.TryGetValue(rel, out var list))
        {
            list = [];
            _Sessions[rel] = list;
        }

        foreach (var o in outcomes)
        {
            list.Add(new SessionEdit(o.FromLine, o.ToLine, o.ReplacementLineCount));
        }
    }

    private static string FormatApplied(
        string rel, int editCount, IReadOnlyList<EditOutcome> outcomes, string newFileHash)
    {
        var sb = new StringBuilder();
        sb.Append("applied ").Append(editCount).Append(" edits to ").AppendLine(rel);
        foreach (var o in outcomes)
        {
            var original = o.ToLine - o.FromLine + 1;
            var range = o.FromLine == o.ToLine ? $"lines {o.FromLine}" : $"lines {o.FromLine}-{o.ToLine}";
            if (o.ReplacementLineCount == 0)
            {
                sb.Append("  - ").Append(range).AppendLine(" → deleted");
            }
            else
            {
                var net = o.ReplacementLineCount - original;
                sb.Append("  - ").Append(range).Append(" → ").Append(o.ReplacementLineCount)
                    .Append(" line").Append(o.ReplacementLineCount == 1 ? string.Empty : "s")
                    .Append(" (").Append(net.ToString("+0;-0;0")).AppendLine(" net)");
            }
        }

        sb.Append("new file hash: ").Append(newFileHash).AppendLine("  (call read_file_with_hashes to verify)");
        return sb.ToString();
    }

    private sealed record SessionEdit(int OriginalStart, int OriginalEnd, int ReplacementLineCount);
}
