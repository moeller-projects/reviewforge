using System.ComponentModel;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>Read-only agent tools for resolve-comment triage.</summary>
/// <remarks>This tool surface deliberately exposes no finding or editing operation.</remarks>
/// <param name="collector">Verdict sink.</param>
/// <param name="allowedThreadIds">Thread ids of the CURRENT triage batch. A verdict for any
/// other thread is rejected: in a multi-batch run the model must not pre-answer a thread it
/// was never shown (a stale actionable verdict would otherwise enter the writable fix plan).
/// Null disables the check (single-shot callers that already confine the prompt).</param>
public sealed class TriageTools(ReviewCollector collector, IReadOnlySet<long>? allowedThreadIds = null)
{
    [Description("Record one triage verdict for an included human comment. Call once per thread.")]
    public string RecordVerdict(
        [Description("Positive pull-request thread id")] int threadId,
        [Description("Verdict: Actionable, NonIssue, Question, AlreadyFixed, or OutOfScope")] string verdict,
        [Description("Evidence from the repository or diff, including file and line when applicable")] string evidence,
        [Description("Confidence: high, medium, or low")] string confidence,
        [Description("Category: bug, security, performance, style, docs, or other")] string category = "bug",
        [Description("Optional concise answer to the commenter")] string? answer = null)
    {
        if (threadId <= 0) return "record_verdict rejected: threadId must be positive";
        if (allowedThreadIds is not null && !allowedThreadIds.Contains(threadId))
            return "record_verdict rejected: thread is not part of this triage batch";
        if (string.IsNullOrWhiteSpace(verdict)) return "record_verdict rejected: verdict is required";
        if (!Enum.TryParse<TriageVerdict>(verdict, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return "record_verdict rejected: verdict must be Actionable, NonIssue, Question, AlreadyFixed, or OutOfScope";
        if (string.IsNullOrWhiteSpace(evidence)) return "record_verdict rejected: evidence is required";
        if (string.IsNullOrWhiteSpace(confidence)) return "record_verdict rejected: confidence is required";
        if (!IsConfidence(confidence)) return "record_verdict rejected: confidence must be high, medium, or low";
        if (string.IsNullOrWhiteSpace(category)) return "record_verdict rejected: category is required";
        if (answer is { Length: > 4000 }) return "record_verdict rejected: answer is too long";

        collector.AddVerdict(new ThreadVerdict(
            threadId,
            parsed,
            evidence.Trim(),
            confidence.Trim().ToLowerInvariant(),
            category.Trim(),
            string.IsNullOrWhiteSpace(answer) ? null : answer.Trim()));
        return $"recorded verdict for thread {threadId}";
    }

    [Description("Finish triage after recording a verdict for every included thread. Call exactly once.")]
    public string TaskDone([Description("Short summary of the triage pass")] string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "task_done rejected: summary is required";
        collector.CompleteTriage(summary.Trim());
        return "triage marked as done";
    }

    private static bool IsConfidence(string value)
        => value.Trim() is "high" or "medium" or "low"
            || value.Trim().Equals("HIGH", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("MEDIUM", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("LOW", StringComparison.OrdinalIgnoreCase);
}
