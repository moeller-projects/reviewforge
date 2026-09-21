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

    private static readonly string[] DefaultDenyPatterns =
    [
        @"\.git(/|$)",
        @"\.env($|\.)", @"\.envrc$",
        @"\.pem$", @"\.key$", @"\.pfx$", @"\.p12$", @"\.snk$",
        @"(^|/)id_(rsa|dsa|ecdsa|ed25519)$",
        @"(^|/|\.)(kube)?config$",          // .kubeconfig, kubeconfig
        @"(^|/)\.aws/",                     // AWS credentials & config
        @"(^|/)\.npmrc$", @"(^|/)\.pypirc$", // registry tokens
        @"(^|/)appsettings\.[^/]+\.json$",  // environment-specific settings (base appsettings.json stays readable)
        @"secrets", @"credentials",
    ];

    private readonly Regex[] _Deny;
    private readonly int _MaxLines;

    private readonly string _Root;

    public RepoReadTools(string rootDir, IEnumerable<string>? denyPatterns = null, int maxLines = DefaultMaxLines)
    {
        _Root = Path.GetFullPath(rootDir);
        _MaxLines = maxLines;
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

        string[] lines;
        try
        {
            lines = ReadAllLines(file);
        }
        catch (IOException ex)
        {
            return $"unreadable: {ex.Message}";
        }

        if (lines.Any(l => l.Contains('\0')))
        {
            return "refused: binary file";
        }

        var start = Math.Max(1, startLine);
        var take = Math.Min(maxLines ?? _MaxLines, _MaxLines);
        if (start > lines.Length)
        {
            return $"file has {lines.Length} lines; startLine {start} is out of range";
        }

        var slice = lines.Skip(start - 1).Take(take).ToArray();
        var sb = new StringBuilder();
        for (var i = 0; i < slice.Length; i++)
        {
            sb.Append(start + i).Append(": ").AppendLine(slice[i]);
        }

        if (start - 1 + slice.Length < lines.Length)
        {
            sb.AppendLine($"…[{lines.Length - (start - 1 + slice.Length)} more lines]");
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
            matcher = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
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
        foreach (var file in Directory.EnumerateFiles(dir, glob ?? "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(_Root, file).Replace('\\', '/');
            if (IsDenied(rel) || !PathSafety.IsContainedReal(_Root, file))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains('\0'))
                {
                    break; // binary
                }

                if (matcher.IsMatch(lines[i]))
                {
                    sb.Append(rel).Append(':').Append(i + 1).Append(": ").AppendLine(lines[i].Trim());
                    if (++matches >= MaxMatches)
                    {
                        sb.AppendLine("…[match cap reached]");
                        return sb.ToString();
                    }
                }
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

    /// <summary>IO seam for tests — production code always hits the filesystem.</summary>
    protected virtual string[] ReadAllLines(string path) => File.ReadAllLines(path);
}