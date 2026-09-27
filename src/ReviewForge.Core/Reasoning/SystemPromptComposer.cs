using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using ReviewForge.Core.Reasoning.Rules;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Loads embedded or configured review and fix-pass prompts and appends the active rule
/// index. Compositions are memoized so the expensive shared prefix (system prompt +
/// rulebook appendix) is byte-identical across runs, letting provider-side prompt caching
/// hit on every request. The cache key includes the override file's last-write timestamp,
/// so editing an override takes effect without a restart; the embedded path never stats
/// the disk.
/// </summary>
public static class SystemPromptComposer
{
    private const string ResourceName = "ReviewForge.Core.Reasoning.Prompts.native-review-system.md";
    private const string FixPassResourceName = "ReviewForge.Core.Reasoning.Prompts.fix-pass-system.md";

    // Keyed by composition kind + source + rulebook version hash + override mtime. The
    // kind prefix separates review and fix-pass compositions of the same file.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Cache = new(StringComparer.Ordinal);

    public static string Compose(string? overridePath = null, RuleBook? ruleBook = null)
        => Memoized("review", overridePath, ruleBook?.VersionHash, () => ComposeCore(overridePath, ruleBook));

    public static string ComposeFixPass(string? overridePath = null)
        => Memoized("fixpass", overridePath, versionHash: null, () => ComposeFixPassCore(overridePath));

    private static string Memoized(string kind, string? overridePath, string? versionHash, Func<string> compose)
    {
        var key = CacheKey(kind, overridePath, versionHash);
        var lazy = Cache.GetOrAdd(
            key,
            _ => new Lazy<string>(compose, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A faulting composition (e.g. override file deleted between the key stat and
            // the read) must not poison the cache: evict only if this exact lazy still
            // owns the slot so a racing success is not discarded, then let the next call
            // retry.
            Cache.TryRemove(new KeyValuePair<string, Lazy<string>>(key, lazy));
            throw;
        }
    }

    private static string CacheKey(string kind, string? overridePath, string? versionHash)
    {
        string source;
        string stamp;
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            source = "<embedded>";
            stamp = "-";
        }
        else
        {
            source = overridePath;
            stamp = File.GetLastWriteTimeUtc(overridePath).Ticks.ToString(CultureInfo.InvariantCulture);
        }

        return string.Concat(kind, "|", source, "|", versionHash ?? "-", "|", stamp);
    }

    private static string ComposeCore(string? overridePath, RuleBook? ruleBook)
    {
        var prompt = !string.IsNullOrWhiteSpace(overridePath)
            ? File.ReadAllText(overridePath)
            : ReadEmbedded();
        if (ruleBook is null) return prompt;
        var sb = new StringBuilder(prompt.TrimEnd());
        sb.AppendLine().AppendLine().AppendLine("# Active rulebook");
        foreach (var pack in ruleBook.Packs)
        {
            sb.AppendLine($"## {pack.Id}: {pack.Title}");
            foreach (var rule in pack.Rules.Where(r => r.Enabled).OrderBy(r => r.Id, StringComparer.Ordinal))
                sb.AppendLine($"- {rule.Id} — {rule.Title}");
        }

        return sb.ToString();
    }

    private static string ComposeFixPassCore(string? overridePath)
        => !string.IsNullOrWhiteSpace(overridePath)
            ? File.ReadAllText(overridePath)
            : ReadEmbedded(FixPassResourceName);

    private static string ReadEmbedded()
        => ReadEmbedded(ResourceName);

    private static string ReadEmbedded(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"embedded prompt resource '{resourceName}' missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
