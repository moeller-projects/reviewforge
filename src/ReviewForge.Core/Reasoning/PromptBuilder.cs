using System.Text;
using ReviewForge.Core.Analysis;
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
    IReadOnlyCollection<string> ContextNames,
    int MaxDiffChars = 200_000,
    int MaxDiffCharsPerFile = 40_000);

/// <summary>
/// Builds the single user prompt for the review run. Deterministic sections so tests
/// can assert on content; full reviews emphasize breadth, follow-ups emphasize delta
/// and open threads.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Stable marker appended wherever the diff is cut short; tells the agent to inspect
    /// the skipped content on its own. Tests assert on this exact text.
    /// </summary>
    internal const string DiffTruncationMarker =
        "…[diff truncated — use repo_read_file / repo_grep to inspect the full change]";

    /// <summary>Delimiter pair marking every PR-author-controlled byte in the prompt.</summary>
    internal const string UntrustedBegin = "<pr-supplied-data>";
    internal const string UntrustedEnd = "</pr-supplied-data>";

    /// <summary>Strip our own delimiters from embedded content so the boundary cannot be forged from inside.</summary>
    internal static string StripDelimiters(string? text)
        => string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace(UntrustedBegin, string.Empty, StringComparison.Ordinal)
                  .Replace(UntrustedEnd, string.Empty, StringComparison.Ordinal);

    public static string Build(PromptInput input)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine(input.Kind == ReviewKind.Full
            ? "# Task: full code review of this pull request"
            : "# Task: follow-up review — the PR changed since the last review; focus on the delta and open threads");
        sb.AppendLine();

        sb.AppendLine("## Pull request");
        sb.AppendLine(UntrustedBegin);
        sb.AppendLine($"- Title: {PromptText.Clean(input.Pr.Title)}");
        if (!string.IsNullOrWhiteSpace(input.Pr.Description))
        {
            sb.AppendLine($"- Description: {PromptText.Clean(input.Pr.Description)}");
        }
        sb.AppendLine(UntrustedEnd);

        // commit SHAs are operator/tool-supplied — outside the delimiters:
        sb.AppendLine($"- Source commit: {input.Pr.SourceCommitSha}");
        sb.AppendLine($"- Target commit: {input.Pr.TargetCommitSha}");
        sb.AppendLine();

        if (input.WorkItems.Count > 0)
        {
            sb.AppendLine("## Linked work items — verify every requirement and acceptance criterion");
            sb.AppendLine(UntrustedBegin);
            foreach (var wi in input.WorkItems)
            {
                sb.AppendLine($"### #{wi.Id} [{PromptText.Clean(wi.Type)}] {PromptText.Clean(wi.Title)} ({PromptText.Clean(wi.State)})");
                if (!string.IsNullOrWhiteSpace(wi.Description))
                {
                    sb.AppendLine(PromptText.Clean(wi.Description.Trim()));
                }

                if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
                {
                    sb.AppendLine("Acceptance criteria:");
                    sb.AppendLine(PromptText.Clean(wi.AcceptanceCriteria.Trim()));
                }
            }
            sb.AppendLine(UntrustedEnd);

            sb.AppendLine();
            sb.AppendLine("For every acceptance criterion, return a verdict (met / unmet / unclear, with evidence) in task_done.");
            sb.AppendLine();
        }

        if (input.PendingReplies.Count > 0)
        {
            sb.AppendLine("## Open threads awaiting your answer");
            sb.AppendLine(UntrustedBegin);
            foreach (var reply in input.PendingReplies)
            {
                sb.AppendLine($"- Thread {reply.ThreadId} (finding {PromptText.Clean(reply.DedupeKey) ?? "n/a"}), {PromptText.Clean(reply.Author)}: {PromptText.Clean(reply.Text)}");
            }
            sb.AppendLine(UntrustedEnd);

            sb.AppendLine();
            sb.AppendLine("Decide per thread in task_done: answer with a comment, resolve it, or reopen it with a follow-up comment.");
            sb.AppendLine();
        }

        sb.AppendLine("## Changed files");
        sb.AppendLine(UntrustedBegin);
        foreach (var file in input.ChangedFiles)
        {
            sb.AppendLine($"- {PromptText.Clean(file)}");
        }
        sb.AppendLine(UntrustedEnd);

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
            sb.AppendLine(StripDelimiters(input.Enrichment.Trim()));
            sb.AppendLine();
        }

        sb.AppendLine("## Unified diff (base → head)");
        sb.AppendLine(UntrustedBegin);
        sb.AppendLine("```diff");
        sb.AppendLine(PromptText.Clean(ShrinkDiff(input.DiffText.Trim(), input.MaxDiffChars, input.MaxDiffCharsPerFile)));
        sb.AppendLine("```");
        sb.AppendLine(UntrustedEnd);

        return sb.ToString();
    }

    /// <summary>
    /// Caps the diff at <c>maxTotal</c> characters overall and <c>maxPerFile</c> per file.
    /// Files over their budget keep the <c>+++ b/</c> header plus a bounded prefix; every
    /// cut is marked with <see cref="DiffTruncationMarker"/> so the agent falls back to
    /// repo_read_file / repo_grep instead of missing the change silently.
    /// </summary>
    internal static string ShrinkDiff(string diff, int maxTotal, int maxPerFile)
    {
        // Per-file content is bounded by the whole diff, so nothing can trip either cap
        // only when both thresholds cover the entire diff.
        if (diff.Length <= maxTotal && maxPerFile >= diff.Length)
        {
            return diff;
        }

        // A fixed '\n' keeps the emitted length equal to the accrued count on every platform,
        // and the closing marker is reserved so the total never exceeds maxTotal.
        const string nl = "\n";
        var markerLength = DiffTruncationMarker.Length + nl.Length;
        var result = new StringBuilder(maxTotal + 4096);
        var currentFileLength = 0;
        var markerWritten = false;
        var written = 0;

        foreach (var lineSpan in diff.AsSpan().EnumerateLines())
        {
            var fileHeader = lineSpan.StartsWith("diff --git ", StringComparison.Ordinal)
                || lineSpan.StartsWith("+++ b/", StringComparison.Ordinal)
                || lineSpan.StartsWith("+++ /dev/null", StringComparison.Ordinal);
            if (fileHeader)
            {
                currentFileLength = 0;
                markerWritten = false;
            }

            if (!fileHeader && currentFileLength >= maxPerFile)
            {
                // This file's body is over budget: keep the header, mark the cut, skip the rest.
                if (!markerWritten && written + markerLength <= maxTotal)
                {
                    result.Append(DiffTruncationMarker).Append(nl);
                    written += markerLength;
                }

                markerWritten = true;
                continue;
            }

            // Reserve room for the truncation marker so the accumulator never exceeds maxTotal.
            if (written + lineSpan.Length + nl.Length + markerLength > maxTotal)
            {
                if (!markerWritten && written + markerLength <= maxTotal)
                {
                    result.Append(DiffTruncationMarker).Append(nl);
                    written += markerLength;
                }

                break;
            }

            result.Append(lineSpan).Append(nl);
            currentFileLength += lineSpan.Length + nl.Length;
            written += lineSpan.Length + nl.Length;
        }

        // Drop the trailing newline so the closing fence lands on its own line.
        var text = result.ToString();
        return text.EndsWith(nl) ? text[..(text.Length - 1)] : text;
    }
}

/// <summary>Single trust boundary for PR-author-controlled text embedded in prompts (P2-29):
/// strips invisible/bidi/tag characters (<see cref="TextSanitizer.Sanitize"/>) and then the
/// prompt delimiter pair (<see cref="PromptBuilder.StripDelimiters"/>) so the boundary cannot
/// be forged from inside. Deliberately does NOT NFKC-normalize — diff/code bytes stay
/// faithful for anchoring (detectors that need the normalized view apply
/// <see cref="PipelineText.Preprocess"/> themselves; anchors resolve against raw file text).</summary>
public static class PromptText
{
    public static string Clean(string? text)
        => PromptBuilder.StripDelimiters(TextSanitizer.Sanitize(text));
}