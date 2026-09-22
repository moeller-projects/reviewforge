using System.ComponentModel;
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

    private static readonly string[] DefaultDenyPatterns =
    [
        @"\.git(/|$)",
        @"\.env($|\.)", @"\.envrc$",
        @"\.pem$", @"\.key$", @"\.pfx$", @"\.p12$", @"\.snk$",
        @"(^|/)id_(rsa|dsa|ecdsa|ed25519)$",
        @"(^|/)\.kube/config$|(^|/|\.)kubeconfig$",
        @"(^|/)\.aws/",                     // AWS credentials & config
        @"(^|/)\.npmrc$", @"(^|/)\.pypirc$", // registry tokens
        @"(^|/)appsettings\.[^/]+\.json$",  // environment-specific settings (base appsettings.json stays readable)
        @"secrets", @"credentials",
    ];

    private static readonly string[] DefaultExcludeDirs =
    [
        "bin", "obj", "node_modules", ".git", ".vs", "packages",
    ];

    private readonly Regex[] _Deny;
    private readonly HashSet<string> _ExcludeDirs;
    private readonly long _MaxGrepFileBytes;
    private readonly int _MaxLines;

    private readonly string _Root;

    public RepoReadTools(
        string rootDir,
        IEnumerable<string>? denyPatterns = null,
        int maxLines = DefaultMaxLines,
        IEnumerable<string>? excludeDirs = null,
        long maxGrepFileBytes = DefaultMaxGrepFileBytes)
    {
        _Root = Path.GetFullPath(rootDir);
        _MaxLines = maxLines;
        _ExcludeDirs = new HashSet<string>(excludeDirs ?? DefaultExcludeDirs, StringComparer.OrdinalIgnoreCase);
        _MaxGrepFileBytes = maxGrepFileBytes;
        _Deny =
        [
            .. (denyPatterns ?? DefaultDenyPatterns)
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        ];
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
        var entries = Directory.GetFileSystemEntries(dir)
            .Select(e => Path.GetRelativePath(_Root, e).Replace('\\', '/'))
            .Where(rel => !IsDenied(rel))
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

    [Description("Search file contents for a regex pattern. Returns matching lines with file and line number.")]
    public string Grep(
        [Description("Regex pattern")] string pattern,
        [Description("Subdirectory to search; empty for whole repo")]
        string? path = null,
        [Description("Optional file glob, e.g. *.cs")]
        string? glob = null)
    {
        Regex matcher;
        try
        {
            // One-shot pattern: interpretation startup is far cheaper than RegexOptions.Compiled
            // codegen; the 2s timeout still guards against catastrophic backtracking.
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
        foreach (var file in EnumerateSearchableFiles(dir, glob ?? "*"))
        {
            var rel = Path.GetRelativePath(_Root, file).Replace('\\', '/');
            if (IsDenied(rel))
            {
                continue;
            }

            try
            {
                // Lexical check is free; only reparse points get the real (stat'ing) check —
                // a symlinked FILE can still point outside the root even under a contained dir.
                if (!PathContainment.IsContained(_Root, file)
                    || (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)
                        && !PathSafety.IsContainedReal(_Root, file))
                    || new FileInfo(file).Length > _MaxGrepFileBytes)
                {
                    continue; // escaping, reparse-point escape, or oversized/generated output
                }

                var lineNo = 0;
                foreach (var line in ReadLinesSafe(file))
                {
                    lineNo++;
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
                            return sb.ToString();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File vanished or became unreadable mid-scan — skip it.
            }
        }

        return matches == 0 ? "no matches" : sb.ToString();
    }

    /// <summary>Resolves a repo-relative path to an absolute path inside the root; null + error when denied or escaping.</summary>
    private string? Resolve(string? relativePath, out string? error)
    {
        error = null;
        var rel = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');

        if (IsDenied(rel))
        {
            error = $"access denied: {relativePath}";
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_Root, rel));
        // Symlink-aware: a checkout-controlled link that resolves outside the root must be
        // refused even though its lexical path stays under _Root.
        if (rel.Length != 0 && !PathSafety.IsContainedReal(_Root, full))
        {
            error = $"access denied: path escapes repository root";
            return null;
        }

        return full;
    }

    private bool IsDenied(string relativePath)
        => _Deny.Any(d => d.IsMatch(relativePath));

    /// <summary>Streaming read seam for tests — production reads line-by-line without materializing the file.</summary>
    protected virtual IEnumerable<string> ReadLinesSafe(string file)
    {
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>Recursive enumeration that never descends into excluded directory names.</summary>
    private IEnumerable<string> EnumerateSearchableFiles(string startDir, string glob)
    {
        var pending = new Stack<string>();
        pending.Push(startDir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(current);
                files = Directory.GetFiles(current, glob);
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
