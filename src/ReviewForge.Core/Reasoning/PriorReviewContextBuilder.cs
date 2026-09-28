using System.Text;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Reasoning;

/// <summary>Formats the prior run's findings joined with their live thread outcomes so the
/// agent sees what previous runs reported and how those threads ended.</summary>
public static class PriorReviewContextBuilder
{
    /// <summary>Name under which prior-review memory is staged.</summary>
    public const string ContextName = "prior-review";

    /// <summary>Maximum number of findings included in prior-review memory.</summary>
    public const int MaxEntries = 40;

    /// <summary>Maximum character length of prior-review memory.</summary>
    public const int MaxPayloadChars = 24_000;

    private const string Header = "# Prior review findings and thread outcomes";
    private const string TruncationMarker = "…[truncated]";

    /// <summary>Null when there is no prior run or it carried no finding rows.</summary>
    public static string? Build(PriorRun? priorRun, IReadOnlyList<ReviewThread> threads)
    {
        if (priorRun?.Findings is not { Count: > 0 } findings)
        {
            return null;
        }

        var liveThreads = threads.ToDictionary(thread => thread.Id);
        var rows = findings
            .Where(finding => !finding.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal))
            .OrderBy(finding => finding.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line ?? 0)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .Take(MaxEntries)
            .Select(finding => Format(finding, liveThreads))
            .ToArray();

        var builder = new StringBuilder(Math.Min(MaxPayloadChars, Header.Length + rows.Sum(row => row.Length + 1)));
        builder.Append(Header);
        foreach (var row in rows)
        {
            if (row.Length > MaxPayloadChars - Header.Length - 1)
            {
                // A single oversized title must not hide later, smaller findings.
                continue;
            }

            if (builder.Length + 1 + row.Length <= MaxPayloadChars)
            {
                builder.Append('\n').Append(row);
                continue;
            }

            var remaining = MaxPayloadChars - builder.Length - 1;
            if (remaining >= TruncationMarker.Length)
            {
                builder.Append('\n').Append(TruncationMarker);
            }

            break;
        }

        return builder.ToString();
    }

    private static string Format(StoredFinding finding, IReadOnlyDictionary<int, ReviewThread> threads)
    {
        var outcome = finding.ThreadId is { } threadId && threads.TryGetValue(threadId, out var thread)
            ? thread.Status switch
            {
                ReviewThreadStatus.Fixed => "fixed",
                ReviewThreadStatus.Closed => "closed-by-author",
                _ => "open",
            }
            : "no-longer-visible";
        var location = finding.FilePath is { Length: > 0 } file
            ? $"{file}:{finding.Line?.ToString() ?? "?"}"
            : $"?:{finding.Line?.ToString() ?? "?"}";
        var title = finding.Title.Replace('\r', ' ').Replace('\n', ' ');
        return $"- [{outcome}] {finding.RuleId} at {location} — \"{title}\"";
    }

}
