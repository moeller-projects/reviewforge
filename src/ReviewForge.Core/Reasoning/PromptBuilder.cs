using System.Text;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>Everything the user prompt is built from — one value, no hidden state.</summary>
public sealed record PromptInput(
    PullRequest Pr,
    ReviewKind Kind,
    IReadOnlyList<WorkItem> WorkItems,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<PendingReply> PendingReplies,
    string DiffText,
    string? Enrichment,
    IReadOnlyCollection<string> ContextNames);

/// <summary>
/// Builds the single user prompt for the review run. Deterministic sections so tests
/// can assert on content; full reviews emphasize breadth, follow-ups emphasize delta
/// and open threads.
/// </summary>
public static class PromptBuilder
{
    public static string Build(PromptInput input)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine(input.Kind == ReviewKind.Full
            ? "# Task: full code review of this pull request"
            : "# Task: follow-up review — the PR changed since the last review; focus on the delta and open threads");
        sb.AppendLine();

        sb.AppendLine("## Pull request");
        sb.AppendLine($"- Title: {input.Pr.Title}");
        if (!string.IsNullOrWhiteSpace(input.Pr.Description))
        {
            sb.AppendLine($"- Description: {input.Pr.Description}");
        }

        sb.AppendLine($"- Source commit: {input.Pr.SourceCommitSha}");
        sb.AppendLine($"- Target commit: {input.Pr.TargetCommitSha}");
        sb.AppendLine();

        if (input.WorkItems.Count > 0)
        {
            sb.AppendLine("## Linked work items — verify every requirement and acceptance criterion");
            foreach (var wi in input.WorkItems)
            {
                sb.AppendLine($"### #{wi.Id} [{wi.Type}] {wi.Title} ({wi.State})");
                if (!string.IsNullOrWhiteSpace(wi.Description))
                {
                    sb.AppendLine(wi.Description.Trim());
                }

                if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
                {
                    sb.AppendLine("Acceptance criteria:");
                    sb.AppendLine(wi.AcceptanceCriteria.Trim());
                }
            }

            sb.AppendLine();
            sb.AppendLine("For every acceptance criterion, return a verdict (met / unmet / unclear, with evidence) in task_done.");
            sb.AppendLine();
        }

        if (input.PendingReplies.Count > 0)
        {
            sb.AppendLine("## Open threads awaiting your answer");
            foreach (var reply in input.PendingReplies)
            {
                sb.AppendLine($"- Thread {reply.ThreadId} (finding {reply.DedupeKey ?? "n/a"}), {reply.Author}: {reply.Text}");
            }

            sb.AppendLine();
            sb.AppendLine("Decide per thread in task_done: answer with a comment, resolve it, or reopen it with a follow-up comment.");
            sb.AppendLine();
        }

        sb.AppendLine("## Changed files");
        foreach (var file in input.ChangedFiles)
        {
            sb.AppendLine($"- {file}");
        }

        sb.AppendLine("You may read unchanged files for dependency context, but every file-specific finding MUST target a changed file and changed line in this pull request. Do not report pre-existing issues from unchanged files or use a general finding to bypass this scope.");
        sb.AppendLine();

        sb.AppendLine();

        if (input.ContextNames.Count > 0)
        {
            sb.AppendLine("## Staged context (read via read_context)");
            foreach (var name in input.ContextNames)
            {
                sb.AppendLine($"- {name}");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(input.Enrichment))
        {
            sb.AppendLine("## Structural enrichment (code-review graph)");
            sb.AppendLine(input.Enrichment.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## Unified diff (base → head)");
        sb.AppendLine("```diff");
        sb.AppendLine(input.DiffText.Trim());
        sb.AppendLine("```");

        return sb.ToString();
    }
}