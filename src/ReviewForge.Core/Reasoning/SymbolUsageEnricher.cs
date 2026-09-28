using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Ports;


namespace ReviewForge.Core.Reasoning;

/// <summary>Enriches reviews with bounded references to symbols introduced by the PR.</summary>
/// <remarks>
/// This is intentionally a filesystem adapter in the reasoning sandbox, like
/// <see cref="RepoReadTools"/>. It applies <see cref="RepoPathGuard"/> and the same
/// deny, symlink, generated-directory, file-size, time, and line budgets as repo tools.
/// </remarks>
public sealed class SymbolUsageEnricher : IContextEnricher
{
    /// <summary>Context-store name for this enrichment.</summary>
    public const string ContextName = "symbol-usage";

    /// <summary>Maximum number of introduced symbols considered.</summary>
    public const int MaxSymbols = 32;

    /// <summary>Maximum external call-site paths shown per symbol.</summary>
    public const int MaxCallSitesPerSymbol = 3;

    /// <summary>Maximum serialized payload size.</summary>
    public const int MaxPayloadChars = 24_000;

    private const long MaxFileBytes = 1_048_576;
    private const int MaxScanMs = 10_000;
    private const int MaxScanLines = 200_000;
    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]{3,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
        "char", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "function", "get", "global", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "let", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "record", "ref", "return", "sbyte", "sealed",
        "set", "short", "sizeof", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void",
        "volatile", "when", "while", "with", "yield", "elif", "fi", "from", "import", "lambda", "pass",
        "raise", "self", "def", "None", "True", "False", "function", "package", "fun", "val", "type"
    };

    /// <inheritdoc />
    public string Name => ContextName;

    /// <inheritdoc />
    public Task<string?> EnrichAsync(string repoDir, string diffText, CancellationToken ct)
        => Task.Run(
            () =>
            {
                try
                {
                    return Build(repoDir, diffText, ct);
                }
                catch
                {
                    return null;
                }
            },
            ct);

    private static string? Build(string repoDir, string diffText, CancellationToken ct)
    {
        if (!Directory.Exists(repoDir))
        {
            return null;
        }

        var symbols = ExtractSymbols(diffText);
        if (symbols.Count == 0)
        {
            return null;
        }

        var guard = new RepoPathGuard(repoDir);
        var root = guard.Root;
        var (rootReal, rootLinks) = guard.ResolveRoot();
        var changed = ExtractChangedFiles(diffText);
        var results = symbols.ToDictionary(s => s, _ => new Usage(), StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var scannedLines = 0;

        foreach (var file in Enumerate(root, guard))
        {
            ct.ThrowIfCancellationRequested();
            if (stopwatch.ElapsedMilliseconds >= MaxScanMs || scannedLines >= MaxScanLines)
            {
                break;
            }

            var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            if (changed.Contains(RepoPath.Normalize(relative)) || file.Length > MaxFileBytes
                || (file.LinkTarget is not null && guard.IsResolvedDenied(file.FullName, rootReal, rootLinks)))
            {
                continue;
            }

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file.FullName))
                {
                    ct.ThrowIfCancellationRequested();
                    lineNumber++;
                    scannedLines++;
                    if (line.Contains('\0'))
                    {
                        break;
                    }

                    foreach (var symbol in symbols)
                    {
                        if (!ReferenceScanner.ScanLines([line], symbol, StringComparison.OrdinalIgnoreCase).Any())
                        {
                            continue;
                        }

                        var usage = results[symbol];
                        usage.Count++;
                        if (usage.Sites.Count < MaxCallSitesPerSymbol)
                        {
                            usage.Sites.Add($"{relative}:{lineNumber}");
                        }
                    }

                    if (stopwatch.ElapsedMilliseconds >= MaxScanMs || scannedLines >= MaxScanLines)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // A file can disappear or become unreadable during a scan; skip it.
            }
        }

        var payload = new StringBuilder("# Symbol usage map (references outside the PR diff)\n");
        foreach (var symbol in symbols)
        {
            var usage = results[symbol];
            if (usage.Count == 0)
            {
                payload.Append("- ").Append(symbol).Append(" — no external references\n");
                continue;
            }

            payload.Append("- ").Append(symbol).Append(" — ").Append(usage.Count).Append(" references: ")
                .Append(string.Join(", ", usage.Sites));
            if (usage.Count > usage.Sites.Count)
            {
                payload.Append(" (+").Append(usage.Count - usage.Sites.Count).Append(" more)");
            }

            payload.Append('\n');
        }

        return payload.Length <= MaxPayloadChars ? payload.ToString() : payload.ToString(0, MaxPayloadChars);
    }

    private static List<string> ExtractSymbols(string diffText)
    {
        var symbols = new List<string>(MaxSymbols);
        foreach (var line in diffText.Split('\n'))
        {
            if (!line.StartsWith('+') || line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Identifier.Matches(line[1..]))
            {
                if (!Keywords.Contains(match.Value) && !symbols.Contains(match.Value, StringComparer.Ordinal))
                {
                    symbols.Add(match.Value);
                    if (symbols.Count == MaxSymbols)
                    {
                        return symbols;
                    }
                }
            }
        }
        return symbols;
    }

    private static HashSet<string> ExtractChangedFiles(string diffText)
    {
        var changed = new HashSet<string>(RepoPath.PathComparer);
        foreach (var line in diffText.Split('\n'))
        {
            if (!line.StartsWith("+++", StringComparison.Ordinal)
                || !DiffPathParser.TryReadToken(line[3..].TrimStart(), out var token, out _)
                || !DiffPathParser.TryDecodeToken(token, out var decoded)
                || !DiffPathParser.TryStripBPrefix(decoded, out var relative))
            {
                continue;
            }

            changed.Add(RepoPath.Normalize(relative.TrimEnd('\r')));
        }

        return changed;
    }

    private static IEnumerable<FileInfo> Enumerate(string root, RepoPathGuard guard)
    {
        var excluded = new HashSet<string>(["bin", "obj", "node_modules", ".git", ".vs", "packages"], StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] dirs;
            FileInfo[] files;
            try
            {
                dirs = Directory.GetDirectories(current);
                files = new DirectoryInfo(current).GetFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in dirs)
            {
                if (!excluded.Contains(Path.GetFileName(directory))
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                }
            }

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                if (!guard.IsDenied(relative) && PathContainment.IsContained(root, file.FullName))
                {
                    yield return file;
                }
            }
        }
    }

    private sealed class Usage
    {
        public int Count { get; set; }
        public List<string> Sites { get; } = [];
    }
}
