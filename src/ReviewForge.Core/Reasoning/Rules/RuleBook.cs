using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReviewForge.Core.Reasoning.Rules;

[ExcludeFromCodeCoverage]
public sealed record RulePack(string Id, string Title, int Version, RuleActivation Activation, IReadOnlyList<Rule> Rules);

[ExcludeFromCodeCoverage]
public sealed record Rule(string Id, string Title, string Description, string Category, string DefaultSeverity, bool Enabled);

[ExcludeFromCodeCoverage]
public sealed record RuleActivation(bool Always, IReadOnlyList<string> Extensions, IReadOnlyList<string> PathPatterns, IReadOnlyList<string> RootFiles);

public sealed class RuleBook
{
    public RuleBook(IReadOnlyList<RulePack> packs)
    {
        Packs = packs;
        Rules = packs.SelectMany(p => p.Rules).Where(r => r.Enabled)
            .ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        VersionHash = ComputeVersionHash(packs);
    }

    public IReadOnlyList<RulePack> Packs { get; }
    public IReadOnlyDictionary<string, Rule> Rules { get; }
    public string VersionHash { get; }

    public bool TryGetRule(string id, out Rule rule) => Rules.TryGetValue(id, out rule!);

    private static string ComputeVersionHash(IEnumerable<RulePack> packs)
    {
        var canonical = string.Join('|', packs.OrderBy(p => p.Id, StringComparer.Ordinal)
            .SelectMany(p => new[] {p.Id, p.Version.ToString()}
                .Concat(p.Rules.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r.Id))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
    }
}

public sealed class RuleBookComposer
{
    private const string ResourcePrefix = "ReviewForge.Core.Reasoning.Rules.";
    private static readonly JsonSerializerOptions JsonOptions = new() {PropertyNameCaseInsensitive = true};

    public RuleBook Compose(IReadOnlyList<string> changedFiles, IReadOnlyList<string> repoRootFiles, string? overridesPath = null)
    {
        var packs = LoadEmbeddedPacks();
        var overrides = LoadOverrides(overridesPath);
        var merged = packs.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overridePack in overrides)
        {
            if (!merged.ContainsKey(overridePack.Id) || replaced.Contains(overridePack.Id))
            {
                if (replaced.Contains(overridePack.Id))
                {
                    var current = merged[overridePack.Id];
                    var rules = current.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var rule in overridePack.Rules) rules[rule.Id] = rule;
                    merged[overridePack.Id] = overridePack with {Rules = rules.Values.ToArray()};
                }
                else
                {
                    merged[overridePack.Id] = overridePack;
                    replaced.Add(overridePack.Id);
                }
            }
            else
            {
                merged[overridePack.Id] = overridePack;
                replaced.Add(overridePack.Id);
            }
        }

        var active = merged.Values.Where(p => IsActive(p, changedFiles, repoRootFiles))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var general = merged.Values.FirstOrDefault(p => p.Id.Equals("general", StringComparison.OrdinalIgnoreCase));
        if (general is null || !general.Rules.Any(r => r.Id.Equals("general.other", StringComparison.OrdinalIgnoreCase) && r.Enabled))
            throw new InvalidOperationException("rulebook must contain enabled rule 'general.other'");
        if (!active.Any(p => p.Id.Equals("general", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("general rule pack must be active");
        return new RuleBook(active);
    }

    private static bool IsActive(RulePack pack, IReadOnlyList<string> changedFiles, IReadOnlyList<string> rootFiles)
    {
        if (pack.Activation.Always) return true;
        var files = changedFiles.Select(Normalize).ToArray();
        return files.Any(file => pack.Activation.Extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                                 || pack.Activation.PathPatterns.Any(pattern => files.Any(file => GlobMatch(pattern, file))))
               || rootFiles.Select(Path.GetFileName).Any(root => pack.Activation.RootFiles.Any(expected => string.Equals(root, expected, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool GlobMatch(string pattern, string value)
    {
        var normalizedPattern = Normalize(pattern);
        var candidate = normalizedPattern.Contains("/", StringComparison.Ordinal)
            ? value
            : Path.GetFileName(value).Replace('\\', '/');
        var regex = "^" + Regex.Escape(normalizedPattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return Regex.IsMatch(candidate, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<RulePack> LoadEmbeddedPacks()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => Deserialize(assembly.GetManifestResourceStream(name)!, name)).ToArray();
    }

    private static IReadOnlyList<RulePack> LoadOverrides(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*.json").OrderBy(p => p, StringComparer.Ordinal)
            .Select(file => Deserialize(File.OpenRead(file), file)).ToArray();
    }

    private static RulePack Deserialize(Stream stream, string source)
    {
        using (stream)
        {
            return JsonSerializer.Deserialize<RulePack>(stream, JsonOptions)
                   ?? throw new InvalidOperationException($"rule pack '{source}' is empty");
        }
    }
}