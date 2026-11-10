using System.Security;
using System.Text;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Resolve.Domain;

namespace ReviewForge.Core.Resolve;

public static class ResolvePromptBuilder
{
    // Other resolve context retains its existing field caps. Linked work-item context is
    // complete: fail visibly rather than send a prompt with omitted comments or fields.
    private const int MaxRequestChars = 2_000;
    private const int MaxHistoryCharsPerThread = 4_000;
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
        AppendWorkItems(sb, workItems);
        return EnsureWithinLimit(sb);
    }

    public static string BuildFixPrompt(IReadOnlyList<PlannedFix> fixes, IReadOnlyList<WorkItem> workItems)
    {
        var sb = new StringBuilder("Apply only the approved minimal changes for the following triaged requests. Files outside the writable set are forbidden.\n");
        sb.AppendLine("Writable files: " + string.Join(", ", fixes.SelectMany(f => f.CandidateFiles).Distinct(StringComparer.Ordinal)));
        AppendUntrusted(sb, "approved-requests", string.Join("\n\n", fixes.Select(f =>
            $"thread={f.ThreadId} anchor={f.Anchor.FilePath}:{f.Anchor.StartLine}-{f.Anchor.EndLine}\nrequest={Truncate(f.RequestText, MaxRequestChars)}\nevidence={Truncate(f.Evidence, MaxRequestChars)}")));
        AppendWorkItems(sb, workItems);
        return EnsureWithinLimit(sb);
    }

    private static void AppendWorkItems(StringBuilder sb, IReadOnlyList<WorkItem> workItems)
    {
        var content = new StringBuilder();
        foreach (var item in workItems)
        {
            content.Append('#').Append(item.Id).Append(" [").Append(item.Type).Append("] ")
                .Append(item.Title).Append(" (").Append(item.State).AppendLine(")");
            AppendField(content, "Description", item.Description);
            AppendField(content, "Acceptance criteria", item.AcceptanceCriteria);
            AppendField(content, "Repro steps", item.ReproSteps);
            AppendField(content, "System info", item.SystemInfo);
            if (item.Comments is { Count: > 0 })
            {
                content.AppendLine("Comments:");
                foreach (var comment in item.Comments)
                {
                    content.AppendLine(FormatWorkItemComment(comment));
                }
            }
        }

        AppendUntrusted(sb, "linked-work-items", content.ToString());
    }

    private static void AppendField(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append(name).AppendLine(":").AppendLine(value);
        }
    }

    private static string FormatWorkItemComment(WorkItemComment comment)
    {
        var metadata = comment.CreatedDate is { } created ? $"created {created:O}" : string.Empty;
        if (comment.ModifiedDate is { } modified
            && (comment.CreatedDate is null || modified > comment.CreatedDate.Value))
        {
            metadata = metadata.Length == 0
                ? $"modified {modified:O}"
                : $"{metadata}; modified {modified:O}";
        }

        var dateText = metadata.Length == 0 ? string.Empty : $" ({metadata})";
        return $"{comment.Author}{dateText}: {comment.Text}";
    }

    private static string EnsureWithinLimit(StringBuilder sb)
    {
        if (sb.Length > MaxTotalPromptChars)
        {
            throw new InvalidOperationException(
                $"Resolve prompt is {sb.Length} characters, exceeding the {MaxTotalPromptChars}-character limit; refusing to omit complete linked work-item context.");
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxChars)
        => value is null || value.Length <= maxChars
            ? value ?? string.Empty
            : string.Concat(value.AsSpan(0, maxChars), TruncationMarker);

    private static void AppendUntrusted(StringBuilder sb, string name, string value)
    {
        sb.Append("<pr-supplied-data name=\"").Append(name).AppendLine("\">");
        sb.AppendLine(SecurityElement.Escape(value));
        sb.AppendLine("</pr-supplied-data>");
    }
}