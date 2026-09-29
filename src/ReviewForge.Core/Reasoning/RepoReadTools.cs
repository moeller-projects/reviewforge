using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ReviewForge.Core.Analysis;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Read-only, in-process filesystem tools for the agent. Rooted at the repository
/// checkout, escape-proof (path containment enforced), deny-regex for secrets and
/// credentials (.git, .env, private keys, cert/keystore material, kube/cloud/CI
/// credentials, environment-specific settings), line-capped reads. No shell tool exists.
/// </summary>
public class RepoReadTools
{
    public const int DefaultMaxLines = 2000;
    public const int MaxMatches = 200;

    /// <summary>Per-file size cap for Grep; larger files are skipped (generated/minified output).</summary>
    public const long DefaultMaxGrepFileBytes = 1_048_576;

    /// <summary>Aggregate wall-clock budget (ms) for one Grep call; the scan aborts with a
    /// truncation marker when exceeded (P2-28).</summary>
    public const int DefaultGrepMaxMs = 10_000;

    /// <summary>Aggregate line budget for one Grep call (P2-28).</summary>
    public const int DefaultGrepMaxLines = 200_000;

    private static readonly string[] DefaultExcludeDirs =
    [
        "bin", "obj", "node_modules", ".git", ".vs", "packages",
    ];

    private readonly RepoPathGuard _Guard;
    private readonly HashSet<string> _ExcludeDirs;
    private readonly long _MaxGrepFileBytes;
    private readonly int _MaxLines;
    private readonly int _GrepMaxMs;
    private readonly int _GrepMaxLines;
    private readonly string? _DiffText;
    private readonly IReadOnlySet<string> _ChangedFiles;
    private readonly DiffIndex? _Diff;
    private IReadOnlyList<DiffBlock>? _DiffBlocks;

    private string Root => _Guard.Root;

    /// <summary>Maximum characters returned by <see cref="FileDiff"/>.</summary>
    public const int MaxFileDiffChars = 20_000;
    /// <summary>Maximum unchanged-file references returned by <see cref="FindReferences"/>.</summary>
    public const int MaxReferenceResults = 20;
    /// <summary>Maximum serialized reference-search response size.</summary>
    public const int MaxReferenceChars = 20_000;

    /// <summary>Creates repository-scoped read and search tools.</summary>
    public RepoReadTools(
        string rootDir,
        IEnumerable<string>? denyPatterns = null,
        int maxLines = DefaultMaxLines,
        IEnumerable<string>? excludeDirs = null,
        long maxGrepFileBytes = DefaultMaxGrepFileBytes,
        int grepMaxMs = DefaultGrepMaxMs,
        int grepMaxLines = DefaultGrepMaxLines,
        string? diffText = null,
        IReadOnlySet<string>? changedFiles = null,
        DiffIndex? diff = null)
    {
        if (grepMaxMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grepMaxMs), grepMaxMs, "Grep time budget must be positive.");
        }

        if (grepMaxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(grepMaxLines), grepMaxLines, "Grep line budget must be positive.");
        }

        _Guard = new RepoPathGuard(rootDir, denyPatterns);
        _MaxLines = maxLines;
        _ExcludeDirs = new HashSet<string>(excludeDirs ?? DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
        _MaxGrepFileBytes = maxGrepFileBytes;
        _GrepMaxMs = grepMaxMs;
        _GrepMaxLines = grepMaxLines;
        _DiffText = diffText;
        _ChangedFiles = changedFiles is null
            ? new HashSet<string>(RepoPath.PathComparer)
            : changedFiles.Select(RepoPath.Normalize).ToHashSet(RepoPath.PathComparer);
        _Diff = diff;
    }

    /// <summary>Shows the diff hunks for one changed file.</summary>
    [Description("Show this pull request's diff hunks for one changed file (what changed, not "
        + "just the current content). Works for deleted files, which repo_read_file cannot serve.")]
    public string FileDiff([Description("File path relative to repo root")] string path)
    {
        var relative = RepoPath.Normalize(path);
        if (_Guard.IsDenied(relative))
        {
            return $"access denied: {path}";
        }

        if (_Diff?.NonReviewableFiles.TryGetValue(relative, out var kind) == true)
        {
            return $"no text diff for '{relative}' ({kind} change)";
        }

        _DiffBlocks ??= DiffBlockSplit.Split(_DiffText ?? string.Empty, out _);
        var block = _DiffBlocks.FirstOrDefault(b =>
            b.File is not null && string.Equals(RepoPath.Normalize(b.File), relative, RepoPath.PathComparison));
        if (block.File is null)
        {
            return $"no diff for '{path}' — not a changed file in this pull request";
        }

        var text = PromptText.Clean(block.Text);
        return text.Length <= MaxFileDiffChars
            ? text
            : text[..MaxFileDiffChars] + "\n…[file diff truncated — use repo_read_file for full content]";
    }


    [Description("List files and directories under a path in the repository.")]
    public string List([Description("Directory path relative to repo root; empty for root")] string? path = null)
    {
        var dir = Resolve(path, out var error);
        if (dir is null)
        {
            return error!;
        }

        if (!Directory.Exists(dir))
        {
            return $"not a directory: {path}";
        }

        var sb = new StringBuilder();
        var (rootReal, rootLinks) = _Guard.ResolveRoot();
        var entries = Directory.GetFileSystemEntries(dir)
            .Select(e => (abs: e, rel: Path.GetRelativePath(Root, e).Replace('\\', '/')))
            .Where(e => !_Guard.IsDenied(e.rel) && !_Guard.IsResolvedDenied(e.abs, rootReal, rootLinks))
            .Select(e => e.rel)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMatches)
            .ToList();

        foreach (var entry in entries)
        {
            sb.AppendLine(entry);
        }

        return entries.Count == 0 ? "(empty)" : sb.ToString();
    }

    [Description("Read a file from the repository (line-capped). Binary and denied files are refused.")]
    public string ReadFile(
        [Description("File path relative to repo root")]
        string path,
        [Description("1-based first line to read")]
        int startLine = 1,
        [Description("Maximum number of lines")]
        int? maxLines = null)
    {
        var file = Resolve(path, out var error);
        if (file is null)
        {
            return error!;
        }

        if (!File.Exists(file))
        {
            return $"not found: {path}";
        }

        var start = Math.Max(1, startLine);
        var take = Math.Min(maxLines ?? _MaxLines, _MaxLines);
        var sb = new StringBuilder();
        var lineNo = 0;
        var emitted = 0;
        try
        {
            foreach (var line in ReadLinesSafe(file))
            {
                lineNo++;
                if (line.Contains('\0'))
                {
                    return "refused: binary file";
                }

                if (lineNo >= start && emitted < take)
                {
                    sb.Append(lineNo).Append(": ").AppendLine(line);
                    emitted++;
                }
            }
        }
        catch (IOException ex)
        {
            return $"unreadable: {ex.Message}";
        }

        if (emitted == 0 && lineNo < start)
        {
            return $"file has {lineNo} lines; startLine {start} is out of range";
        }

        if (lineNo >= start + emitted)
        {
            sb.AppendLine($"…[{lineNo - (start - 1 + emitted)} more lines]");
        }

        return sb.ToString();
    }

    [Description("Search file contents for a regex pattern. Returns matching lines with file and line number. "
        + "Stops with a \"…[truncated: budget-time]\" or \"…[truncated: budget-lines]\" marker when the aggregate "
        + "time/line budget is reached — narrow the pattern or path and retry. \"…[pattern-fallback]\" means the "
        + "pattern needed lookarounds/backreferences, so it runs with a per-line timeout instead of the "
        + "linear-time engine.")]
    public string Grep(
        [Description("Regex pattern")] string pattern,
        [Description("Subdirectory to search; empty for whole repo")] string? path = null,
        [Description("Optional file glob, e.g. *.cs")] string? glob = null,
        CancellationToken cancellationToken = default)
    {
        Regex matcher;
        var patternFallback = false;
        try
        {
            // Linear-time engine: a model-supplied catastrophic pattern cannot backtrack
            // (P2-28). Constructs it rejects (lookarounds, backreferences) fall back below.
            matcher = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            patternFallback = true;
            matcher = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return $"invalid pattern: {ex.Message}";
        }

        var dir = Resolve(path, out var error);
        if (dir is null)
        {
            return error!;
        }

        if (!Directory.Exists(dir))
        {
            return $"not a directory: {path}";
        }

        var sb = new StringBuilder();
        var matches = 0;
        var totalLines = 0;
        string? budget = null;
        var (rootReal, rootLinks) = _Guard.ResolveRoot();
        var stopwatch = Stopwatch.StartNew();
        foreach (var file in EnumerateSearchableFiles(dir, glob ?? "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = file.FullName;
            var rel = Path.GetRelativePath(Root, full).Replace('\\', '/');
            if (_Guard.IsDenied(rel))
            {
                continue;
            }

            try
            {
                if (!PathContainment.IsContained(Root, full))
                {
                    continue; // escaping
                }

                // A symlinked file can point at a contained-but-denied target, so the deny
                // list is re-applied to the resolved path; symlink-traversed files are
                // refused outright (P1-15). Resolution (per-component stat'ing) is
                // reserved for symlinked files only: enumeration never descends into
                // reparse-point directories, so a regular file (LinkTarget null) shares
                // the root's outside-the-checkout prefix — it resolves to itself, and
                // IsResolvedDenied would reduce to the lexical IsDenied already applied.
                if ((file.LinkTarget is not null && _Guard.IsResolvedDenied(full, rootReal, rootLinks))
                    || file.Length > _MaxGrepFileBytes)
                {
                    continue; // resolved-denied, symlinked, or oversized/generated output
                }

                var lineNo = 0;
                foreach (var line in ReadLinesSafe(full))
                {
                    lineNo++;
                    totalLines++;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalLines >= _GrepMaxLines || stopwatch.ElapsedMilliseconds >= _GrepMaxMs)
                    {
                        budget = totalLines >= _GrepMaxLines ? "budget-lines" : "budget-time";
                        break;
                    }

                    if (line.Contains('\0'))
                    {
                        break; // binary
                    }

                    if (matcher.IsMatch(line))
                    {
                        sb.Append(rel).Append(':').Append(lineNo).Append(": ").AppendLine(line.Trim());
                        if (++matches >= MaxMatches)
                        {
                            sb.AppendLine("…[match cap reached]");
                            return AppendTrailers(sb, patternFallback);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File vanished or became unreadable mid-scan — skip it.
            }

            if (budget is not null)
            {
                break;
            }
        }

        if (matches == 0 && budget is null)
        {
            return "no matches";
        }

        if (budget is not null)
        {
            sb.AppendLine($"…[truncated: {budget}]");
        }

        return AppendTrailers(sb, patternFallback);
    }

    /// <summary>Finds usages of an identifier across unchanged repository files.</summary>
    [Description("Find usages of an identifier across the repository's unchanged files. Use "
        + "BEFORE claiming code is unused, unreferenced, or signature-mismatched. Files changed "
        + "in this PR are excluded — inspect them with repo_file_diff / repo_read_file.")]
    public string FindReferences(
        [Description("Identifier to find (e.g. ParseConfig or Config.Parse)")] string identifier,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceScanner.IsValidIdentifier(identifier))
        {
            return "invalid identifier";
        }

        var sb = new StringBuilder();
        var shown = 0;
        var total = 0;
        var changedHits = 0;
        var totalLines = 0;
        string? budget = null;
        var (rootReal, rootLinks) = _Guard.ResolveRoot();
        var stopwatch = Stopwatch.StartNew();
        foreach (var file in EnumerateSearchableFiles(Root, "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = file.FullName;
            var rel = RepoPath.Normalize(Path.GetRelativePath(Root, full).Replace('\\', '/'));
            if (_Guard.IsDenied(rel)
                || (file.LinkTarget is not null && _Guard.IsResolvedDenied(full, rootReal, rootLinks))
                || file.Length > _MaxGrepFileBytes)
            {
                continue;
            }

            var changed = _ChangedFiles.Contains(rel);
            try
            {
                var lineNo = 0;
                foreach (var line in ReadLinesSafe(full))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalLines >= _GrepMaxLines || stopwatch.ElapsedMilliseconds >= _GrepMaxMs)
                    {
                        budget = totalLines >= _GrepMaxLines ? "budget-lines" : "budget-time";
                        break;
                    }

                    lineNo++;
                    totalLines++;

                    if (line.Contains('\0'))
                    {
                        break;
                    }

                    foreach (var _ in ReferenceScanner.ScanLines([line], identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        if (changed)
                        {
                            changedHits++;
                        }
                        else
                        {
                            total++;
                        }
                        if (!changed && shown < MaxReferenceResults)
                        {
                            sb.Append(rel).Append(':').Append(lineNo).Append(": ")
                                .AppendLine(PromptText.Clean(line.Trim()));
                            shown++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File vanished or became unreadable during the scan.
            }

            if (budget is not null)
            {
                break;
            }
        }

        if (total == 0 && changedHits == 0 && budget is null)
        {
            return "no references";
        }

        if (budget is not null)
        {
            sb.AppendLine($"…[truncated: {budget}]");
        }

        if (total > 0 || changedHits > 0)
        {
            sb.Append(total).Append(", showing first ").Append(MaxReferenceResults).Append(" references");
            if (changedHits > 0)
            {
                sb.Append(" (+").Append(changedHits).Append(" in PR-changed files)");
            }
            sb.AppendLine();
        }

        var output = sb.ToString();
        if (output.Length <= MaxReferenceChars)
        {
            return output;
        }

        const string marker = "…[truncated: result-chars]\n";
        return output[..(MaxReferenceChars - marker.Length)] + marker;
    }

    private static string AppendTrailers(StringBuilder sb, bool patternFallback)
    {
        if (patternFallback)
        {
            sb.AppendLine("…[pattern-fallback]");
        }

        return sb.ToString();
    }

    /// <summary>Resolves a repo-relative path to an absolute path inside the root; null + error when denied or escaping.</summary>
    private string? Resolve(string? relativePath, out string? error)
        => _Guard.Resolve(relativePath, out error);

    /// <summary>Streaming read seam for tests — production reads line-by-line without materializing the file.</summary>
    protected virtual IEnumerable<string> ReadLinesSafe(string file)
    {
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Recursive enumeration that never descends into excluded directory names.
    /// Yields FileInfo so the per-file size check reuses the enumeration's stat instead of
    /// re-statting (P2-28).</summary>
    private IEnumerable<FileInfo> EnumerateSearchableFiles(string startDir, string glob)
    {
        var pending = new Stack<string>();
        pending.Push(startDir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] subdirs;
            FileInfo[] files;
            try
            {
                subdirs = Directory.GetDirectories(current);
                files = new DirectoryInfo(current).GetFiles(glob);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                if (_ExcludeDirs.Contains(Path.GetFileName(sub)))
                {
                    continue;
                }

                // Never descend into a reparse point (symlinked directory): it can point
                // outside the workspace root and bypass lexical containment.
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(sub);
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
