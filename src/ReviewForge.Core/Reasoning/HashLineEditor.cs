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
    private readonly HashSet<string> _Writable;
    private readonly Dictionary<string, SessionRange> _Sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _RawCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _OriginalRaw = new(StringComparer.Ordinal);

    public HashLineEditor(RepoPathGuard guard, IReadOnlySet<string> writableRelativePaths)
    {
        _Guard = guard;
        _Writable = new HashSet<string>(
            writableRelativePaths.Select(RepoPath.Normalize), StringComparer.Ordinal);
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

        var lines = raw ?? throw new InvalidOperationException("ReadRawLines returned no error but no lines");
        var start = Math.Max(1, startLine);
        var take = Math.Min(maxLines ?? MaxLines, MaxLines);
        var hashes = lines.Select(HashLine.Of).ToArray();
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
        for (var i = start - 1; i < lines.Count && emitted < take; i++, emitted++)
        {
            var marker = duplicated.Contains(hashes[i]) ? "*" : string.Empty;
            sb.Append(i + 1).Append(' ').Append(hashes[i]).Append(marker).Append(": ")
                .AppendLine(HashLine.Normalize(lines[i] ?? string.Empty));
        }

        if (emitted == 0 && lines.Count < start)
        {
            return $"file has {lines.Count} lines; startLine {start} is out of range";
        }

        if (lines.Count > start - 1 + emitted)
        {
            sb.AppendLine($"…[{lines.Count - (start - 1 + emitted)} more lines]");
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

        WriteAtomic(rel, full!, newLines, newLine!);
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

        if (!EnsureStablePath(rel, full, out var error))
        {
            throw new InvalidOperationException(error ?? $"access denied: {relativePath}");
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

        WriteAtomic(rel, full!, newLines, newLine!);
        RecordSession(rel, result.Outcomes);
        _RawCache.Remove(rel);
        return result;
    }

    /// <summary>
    /// Writes content lines using the original raw snapshot when they restore the
    /// session's original content; otherwise the dominant line ending is used.
    /// Writes outside the writable set fail.
    /// </summary>
    public virtual void WriteAllLines(string relativePath, string[] lines)
    {
        if (!TryResolveWritable(relativePath, out var rel, out var full, out var denyError))
        {
            throw new InvalidOperationException(denyError ?? $"access denied: {relativePath}");
        }

        if (TryGetOriginalBytes(rel, lines, out var original))
        {
            WriteAtomic(rel, full!, original);
            return;
        }

        string[]? cached = null;
        _RawCache.TryGetValue(rel, out cached);
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

        WriteAtomic(rel, full!, string.Join(newLine, lines) + newLine);
    }

    /// <summary>The merged change this editor session produced on a file: the contiguous
    /// region from the first to the last edited line, with its CURRENT content as
    /// Replacement. Null when the session made no edits to the file.</summary>
    public virtual (int StartLine, int EndLine, string Replacement)? GetSessionChange(string relativePath)
    {
        var rel = RepoPath.Normalize(relativePath);
        if (!_Sessions.TryGetValue(rel, out var range))
        {
            return null;
        }

        var full = _Guard.Resolve(rel, out _);
        if (full is null || !File.Exists(full) || !EnsureStablePath(rel, full, out _))
        {
            return null;
        }

        var (raw, error) = ReadRawLines(rel, full);
        if (error is not null)
        {
            return null;
        }

        var current = raw!.Select(HashLine.Normalize).ToArray();
        var length = range.EndLine >= range.StartLine
            ? range.EndLine - range.StartLine + 1
            : 0;
        var replacement = string.Join('\n', current.Skip(Math.Max(0, range.StartLine - 1)).Take(length));
        return (range.StartLine, range.EndLine, replacement);
    }

    private bool TryResolveWritable(string path, out string rel, out string? full, out string? error)
    {
        rel = RepoPath.Normalize(path ?? string.Empty);
        full = _Guard.Resolve(rel, out error);
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

        if (!EnsureStablePath(rel, full, out error))
        {
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

        if (!EnsureStablePath(rel, full, out var pathError))
        {
            return (null, pathError);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (IOException)
        {
            return (null, $"unreadable: {rel}");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, $"unreadable: {rel}");
        }

        if (!_OriginalRaw.ContainsKey(rel))
        {
            _OriginalRaw[rel] = bytes;
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

    private void WriteAtomic(string rel, string full, string[] lines, string newLine)
        => WriteAtomic(rel, full, string.Join(newLine, lines) + newLine);

    // File.Move(temp, full, overwrite: true) atomically replaces the destination entry,
    // rather than following a destination symlink. Stable-path checks still have a residual
    // race window between validation and replacement.
    private void WriteAtomic(string rel, string full, string text)
    {
        if (!EnsureStablePath(rel, full, out var error))
        {
            throw new IOException(error);
        }

        var dir = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(dir, ".rf-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, text);
            if (!EnsureStablePath(rel, full, out error))
            {
                throw new IOException(error);
            }

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

    private void WriteAtomic(string rel, string full, byte[] bytes)
    {
        if (!EnsureStablePath(rel, full, out var error))
        {
            throw new IOException(error);
        }

        var dir = Path.GetDirectoryName(full)!;
        var temp = Path.Combine(dir, ".rf-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (!EnsureStablePath(rel, full, out error))
            {
                throw new IOException(error);
            }

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

    private bool EnsureStablePath(string rel, string full, out string? error)
    {
        error = null;
        try
        {
            var attributes = File.GetAttributes(full);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"access denied: {rel}";
                return false;
            }
        }
        catch (FileNotFoundException)
        {
            error = $"not found: {rel}";
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            error = $"not found: {rel}";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"unreadable: {rel}";
            return false;
        }

        var resolved = _Guard.Resolve(rel, out _);
        if (resolved is null ||
            !string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(full), StringComparison.Ordinal))
        {
            error = $"access denied: {rel}";
            return false;
        }

        return true;
    }

    private bool TryGetOriginalBytes(string rel, IReadOnlyList<string> lines, out byte[] bytes)
    {
        bytes = [];
        if (!_OriginalRaw.TryGetValue(rel, out var original))
        {
            return false;
        }

        var originalLines = SplitKeepEndings(Encoding.UTF8.GetString(original));
        if (originalLines.Length != lines.Count ||
            originalLines.Where((line, index) =>
                !string.Equals(HashLine.Normalize(line), lines[index], StringComparison.Ordinal)).Any())
        {
            return false;
        }

        bytes = original;
        return true;
    }

    private void RecordSession(string rel, IReadOnlyList<EditOutcome> outcomes)
    {
        foreach (var o in outcomes.OrderByDescending(o => o.FromLine))
        {
            var f = o.FromLine;
            var t = o.ToLine;
            var replacement = o.ReplacementLineCount;
            if (!_Sessions.TryGetValue(rel, out var current))
            {
                _Sessions[rel] = new SessionRange(f, f + replacement - 1);
                continue;
            }

            var delta = replacement - (t - f + 1);
            if (f > current.EndLine)
            {
                _Sessions[rel] = current with { EndLine = Math.Max(current.EndLine, f + replacement - 1) };
            }
            else if (current.StartLine > t)
            {
                _Sessions[rel] = new SessionRange(
                    current.StartLine + delta, current.EndLine + delta);
            }
            else
            {
                var shiftedStart = ShiftBoundary(current.StartLine, f, t, delta);
                var shiftedEnd = ShiftBoundary(current.EndLine, f, t, delta);
                _Sessions[rel] = new SessionRange(
                    Math.Min(shiftedStart, f),
                    Math.Max(shiftedEnd, f + replacement - 1));
            }
        }
    }

    private static int ShiftBoundary(int boundary, int from, int to, int delta)
        => boundary > to ? boundary + delta : boundary >= from ? from : boundary;


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

    private sealed record SessionRange(int StartLine, int EndLine);
}
