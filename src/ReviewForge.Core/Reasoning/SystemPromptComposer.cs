using System.Reflection;
using System.Text;
using ReviewForge.Core.Reasoning.Rules;

namespace ReviewForge.Core.Reasoning;

/// <summary>Loads embedded or configured review and fix-pass prompts and appends the active rule index.</summary>
public static class SystemPromptComposer
{
    private const string ResourceName = "ReviewForge.Core.Reasoning.Prompts.native-review-system.md";
    private const string FixPassResourceName = "ReviewForge.Core.Reasoning.Prompts.fix-pass-system.md";

    public static string Compose(string? overridePath = null, RuleBook? ruleBook = null)
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

    public static string ComposeFixPass(string? overridePath = null)
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