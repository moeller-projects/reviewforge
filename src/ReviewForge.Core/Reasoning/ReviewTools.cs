using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning.Rules;

namespace ReviewForge.Core.Reasoning;

/// <summary>Agent-facing review tools for reading context and recording validated review output.</summary>
public sealed class ReviewTools(
    ReviewCollector collector,
    ContextStore contextStore,
    RuleBook? ruleBook = null,
    IReadOnlySet<string>? changedFiles = null,
    DiffIndex? diff = null)
{
    private readonly IReadOnlySet<string>? _ChangedFiles = changedFiles;
    private readonly DiffIndex? _Diff = diff;
    private readonly RuleBook? _RuleBook = ruleBook;

    [Description("Read the active rulebook or one active pack. Full descriptions are returned here.")]
    public string GetRulebook(string? pack = null)
    {
        if (_RuleBook is null) return "rulebook unavailable";
        if (pack is not null)
        {
            var selected = _RuleBook.Packs.FirstOrDefault(p => p.Id.Equals(pack, StringComparison.OrdinalIgnoreCase));
            return selected is null
                ? $"unknown rule pack '{pack}'. Active packs: {string.Join(", ", _RuleBook.Packs.Select(p => p.Id))}"
                : FormatPack(selected);
        }

        return string.Join(Environment.NewLine, _RuleBook.Packs.Select(FormatPack));
    }

    private static string FormatPack(RulePack pack)
        => $"## {pack.Id}: {pack.Title}{Environment.NewLine}" +
           string.Join(Environment.NewLine, pack.Rules.Where(r => r.Enabled).Select(r => $"- {r.Id} — {r.Title} ({r.DefaultSeverity}): {r.Description}"));

    [Description("Read a staged context entry (enrichment payload, code-review-graph output). List available names first via the prompt context.")]
    public string ReadContext([Description("Name of the context entry")] string name)
        => contextStore.Read(name) ?? $"unknown context entry '{name}'. Available: {string.Join(", ", contextStore.Names)}";

    [Description("Record a single review finding. Call once per distinct issue. Duplicate findings are rejected.")]
    public string RecordFinding(
        [Description("Stable rule id")] string ruleId,
        [Description("One-line title of the issue")]
        string title,
        [Description("Severity: critical, high, medium, low or info")]
        string severity,
        [Description("Category: bug, security, performance, style or docs")]
        string category,
        [Description("What is wrong and why it matters")]
        string description,
        [Description("Exact offending code line(s)")]
        string? snippet = null,
        [Description("Concrete fix suggestion")]
        string? suggestion = null,
        [Description("File path relative to repo root; required for pull-request findings")]
        string? filePath = null,
        [Description("1-based start line")] int? startLine = null,
        [Description("1-based end line; defaults to startLine")]
        int? endLine = null)
    {
        var finding = new RichFinding
        {
            RuleId = ruleId ?? string.Empty,
            Title = title ?? string.Empty,
            Severity = severity ?? string.Empty,
            Category = category ?? string.Empty,
            Description = description ?? string.Empty,
            Snippet = snippet,
            Suggestion = suggestion,
            Anchor = filePath is null ? null : new FindingAnchor(filePath, startLine ?? 0, endLine ?? startLine ?? 0),
        };

        var errors = Validate(finding);
        if (errors.Count > 0) return "finding rejected: " + string.Join("; ", errors);
        if (_ChangedFiles is not null)
        {
            if (finding.Anchor is not { } anchor)
                return "finding rejected: a changed file and line are required for this pull-request review";

            var path = Normalize(anchor.FilePath);
            if (!_ChangedFiles.Contains(path))
                return $"finding rejected: '{anchor.FilePath}' is outside the current pull-request diff";
            if (_Diff is not null && !_Diff.Contains(path, anchor.StartLine))
                return $"finding rejected: line {anchor.StartLine} in '{anchor.FilePath}' is outside the current pull-request diff";
        }

        if (_RuleBook is not null && !_RuleBook.TryGetRule(finding.RuleId, out _))
        {
            var index = string.Join(", ", _RuleBook.Rules.Values.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id} — {r.Title}"));
            return $"unknown rule '{finding.RuleId}'. Active rules: {index}. Use 'general.other' if none fits.";
        }

        var key = DedupeKey.Compute(finding.RuleId, finding.Anchor?.FilePath ?? "-", finding.Snippet);
        if (collector.IsKnown(key)) return $"already recorded (dedupe key {key}) — skipped";
        finding.DedupeKey = key;
        collector.AddFinding(finding);
        return $"recorded finding {key}";
    }

    [Description("Record an open question or uncertainty you could not resolve from the code.")]
    public string RecordUncertainty(string topic, string question, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(question)) return "uncertainty rejected: topic and question are required";
        collector.AddUncertainty(new ReviewUncertainty(topic, question, context));
        return "recorded uncertainty";
    }

    [Description("Finish the review. Call exactly once when done — no further tool calls afterwards.")]
    public string TaskDone(string reviewSummary, string? verificationSummary = null, string? prSummary = null,
        string[]? goodPractices = null, AcVerdict[]? acceptanceCriteria = null, ThreadAction[]? threadActions = null)
    {
        if (string.IsNullOrWhiteSpace(reviewSummary)) return "task_done rejected: reviewSummary is required";
        var narrative = new ReviewNarrative {ReviewSummary = reviewSummary, VerificationSummary = verificationSummary, PrSummary = prSummary, GoodPractices = goodPractices, AcceptanceCriteria = acceptanceCriteria, ThreadActions = threadActions};
        var errors = Validate(narrative);
        if (errors.Count > 0) return "task_done rejected: " + string.Join("; ", errors);
        collector.Complete(narrative);
        return "review marked as done";
    }

    private static List<string> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, true);
        var errors = results.Select(r => r.ErrorMessage ?? "invalid").ToList();
        switch (instance)
        {
            case RichFinding {Anchor: { } anchor}:
                var anchorResults = new List<ValidationResult>();
                Validator.TryValidateObject(anchor, new ValidationContext(anchor), anchorResults, true);
                errors.AddRange(anchorResults.Select(r => r.ErrorMessage ?? "invalid anchor"));
                break;
            case ReviewNarrative narrative:
                foreach (var item in (narrative.AcceptanceCriteria ?? []).Cast<object>().Concat(narrative.ThreadActions ?? []))
                {
                    var itemResults = new List<ValidationResult>();
                    Validator.TryValidateObject(item, new ValidationContext(item), itemResults, true);
                    errors.AddRange(itemResults.Select(r => r.ErrorMessage ?? "invalid item"));
                }

                break;
        }

        return errors;
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/').TrimStart('/');
}