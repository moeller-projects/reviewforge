using System.Security;
using System.Text;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

public static class ResolvePromptBuilder
{
    public static string BuildTriagePrompt(IReadOnlyList<ResolvableComment> comments, IReadOnlyList<WorkItem> workItems)
    {
        var sb = new StringBuilder("Triage each supplied comment using repository evidence. Return exactly one verdict per thread.\n\n");
        AppendUntrusted(sb, "threads", string.Join("\n", comments.Select(comment =>
            $"thread={comment.ThreadId} anchor={comment.Anchor?.FilePath}:{comment.Anchor?.StartLine}-{comment.Anchor?.EndLine}\nrequester={comment.RequesterName} ({comment.RequesterId})\nrequest={comment.RequestText}\nhistory:\n{string.Join("\n", comment.History.Select(h => $"{h.AuthorName}: {h.Text}"))}"))));
        AppendUntrusted(sb, "linked-work-items", string.Join("\n", workItems.Select(item =>
            $"#{item.Id} {item.Title}\nAcceptance criteria: {item.AcceptanceCriteria}")));
        return sb.ToString();
    }

    public static string BuildFixPrompt(IReadOnlyList<PlannedFix> fixes, IReadOnlyList<WorkItem> workItems)
    {
        var sb = new StringBuilder("Apply only the approved minimal changes for the following triaged requests. Files outside the writable set are forbidden.\n");
        sb.AppendLine("Writable files: " + string.Join(", ", fixes.SelectMany(f => f.CandidateFiles).Distinct(StringComparer.Ordinal)));
        AppendUntrusted(sb, "approved-requests", string.Join("\n\n", fixes.Select(f =>
            $"thread={f.ThreadId} anchor={f.Anchor.FilePath}:{f.Anchor.StartLine}-{f.Anchor.EndLine}\nrequest={f.RequestText}\nevidence={f.Evidence}")));
        AppendUntrusted(sb, "linked-work-items", string.Join("\n", workItems.Select(item => $"#{item.Id} {item.Title}\n{item.AcceptanceCriteria}")));
        return sb.ToString();
    }

    private static void AppendUntrusted(StringBuilder sb, string name, string value)
    {
        sb.Append("<pr-supplied-data name=\"").Append(name).AppendLine("\">");
        sb.AppendLine(SecurityElement.Escape(value) ?? string.Empty);
        sb.AppendLine("</pr-supplied-data>");
    }
}
