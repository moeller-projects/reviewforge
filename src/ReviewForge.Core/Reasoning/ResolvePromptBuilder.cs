using System.Security;
using System.Text;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

public static class ResolvePromptBuilder
{
    // PR-supplied text is unbounded upstream; the initial prompt string must stay within the
    // model context even before the agent can compact. Per-field caps keep threads readable;
    // the total cap is the hard guarantee. Truncation markers direct triage to repository
    // evidence for the elided remainder.
    private const int MaxRequestChars = 2_000;
    private const int MaxHistoryCharsPerThread = 4_000;
    private const int MaxWorkItemsChars = 8_000;
    private const int MaxTotalPromptChars = 64_000;
    private const string TruncationMarker = "… [truncated — inspect the repository for the full context]";

    public static string BuildTriagePrompt(IReadOnlyList<ResolvableComment> comments, IReadOnlyList<WorkItem> workItems)
    {
        var sb = new StringBuilder("Triage each supplied comment using repository evidence. Return exactly one verdict per thread.\n\n");
        var threads = string.Join("\n", comments.Select(comment =>
        {
            var history = Truncate(
                string.Join("\n", comment.History.Select(h => $"{h.AuthorName}: {h.Text}")),
                MaxHistoryCharsPerThread);
            return $"thread={comment.ThreadId} anchor={comment.Anchor?.FilePath}:{comment.Anchor?.StartLine}-{comment.Anchor?.EndLine}\nrequester={comment.RequesterName} ({comment.RequesterId})\nrequest={Truncate(comment.RequestText, MaxRequestChars)}\nhistory:\n{history}";
        }));
        AppendUntrusted(sb, "threads", threads);
        AppendUntrusted(sb, "linked-work-items", Truncate(string.Join("\n", workItems.Select(item =>
            $"#{item.Id} {item.Title}\nAcceptance criteria: {item.AcceptanceCriteria}")), MaxWorkItemsChars));
        return Truncate(sb.ToString(), MaxTotalPromptChars);
    }

    public static string BuildFixPrompt(IReadOnlyList<PlannedFix> fixes, IReadOnlyList<WorkItem> workItems)
    {
        var sb = new StringBuilder("Apply only the approved minimal changes for the following triaged requests. Files outside the writable set are forbidden.\n");
        sb.AppendLine("Writable files: " + string.Join(", ", fixes.SelectMany(f => f.CandidateFiles).Distinct(StringComparer.Ordinal)));
        AppendUntrusted(sb, "approved-requests", string.Join("\n\n", fixes.Select(f =>
            $"thread={f.ThreadId} anchor={f.Anchor.FilePath}:{f.Anchor.StartLine}-{f.Anchor.EndLine}\nrequest={Truncate(f.RequestText, MaxRequestChars)}\nevidence={Truncate(f.Evidence, MaxRequestChars)}")));
        AppendUntrusted(sb, "linked-work-items", Truncate(string.Join("\n", workItems.Select(item => $"#{item.Id} {item.Title}\n{item.AcceptanceCriteria}")), MaxWorkItemsChars));
        return Truncate(sb.ToString(), MaxTotalPromptChars);
    }

    private static string Truncate(string? value, int maxChars)
        => value is null || value.Length <= maxChars
            ? value ?? string.Empty
            : string.Concat(value.AsSpan(0, maxChars), TruncationMarker);

    private static void AppendUntrusted(StringBuilder sb, string name, string value)
    {
        sb.Append("<pr-supplied-data name=\"").Append(name).AppendLine("\">");
        sb.AppendLine(SecurityElement.Escape(value) ?? string.Empty);
        sb.AppendLine("</pr-supplied-data>");
    }
}
