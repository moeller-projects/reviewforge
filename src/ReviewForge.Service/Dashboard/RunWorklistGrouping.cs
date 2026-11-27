using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Bounded worklist projection. Metadata may be unavailable for closed PRs; the full
/// PR key and run remain navigable. Finding counts describe this run, not carried identities.</summary>
public sealed record RunWorklistItem(PrKey Pr, DashboardRun Run,
    string? Title = null, string? RepositoryName = null, string? BrowserUrl = null,
    string? CurrentHeadSha = null, FindingObservationCounts? Findings = null,
    int? PendingDecisions = null, int? ApprovedUnpublished = null)
{
    public bool NeedsAttention => Run.State is RunState.Queued or RunState.Running or RunState.Failed
        || (Run.State is null && (Run.IsClaimed || Run.IsPending || Run.IsIncomplete))
        || (Run.Kind == RunKind.ReviewDraft && Run.State == RunState.Completed
            && (PendingDecisions > 0 || ApprovedUnpublished > 0));

    public DateTimeOffset? ObservedAt => Run.EnqueuedAt ?? Run.StartedAt ?? Run.UpdatedAt ?? Run.CompletedAt;
}

public sealed record RunWorklistRevision(string? HeadSha, DateTimeOffset? LastObservedAt,
    bool IsCurrentHead, IReadOnlyList<RunWorklistItem> Runs)
{
    public bool NeedsAttention => Runs.Any(r => r.NeedsAttention);
    public bool InitiallyExpanded => NeedsAttention;
}

public sealed record RunWorklistPrGroup(PrKey Pr, string? Title, string? RepositoryName,
    string? BrowserUrl, IReadOnlyList<RunWorklistRevision> Revisions)
{
    public bool NeedsAttention => Revisions.Any(r => r.NeedsAttention);
    public DateTimeOffset? LastObservedAt => Revisions.FirstOrDefault()?.LastObservedAt;
}

/// <summary>Presentation grouping only. Revision ordering is newest OBSERVED run chronology,
/// not provider revision numbering or SHA ordering. An attention-only view retains older
/// history inside matching PR groups so it can remain collapsed, rather than disappearing.</summary>
public static class RunWorklistGrouping
{
    public static IReadOnlyList<RunWorklistPrGroup> Group(IReadOnlyList<RunWorklistItem> items, bool attentionOnly)
        => items.GroupBy(i => i.Pr).Select(group =>
        {
            var ordered = group.OrderByDescending(i => i.ObservedAt).ThenByDescending(i => i.Run.RunId).ToArray();
            var currentHead = ordered.Select(i => i.CurrentHeadSha).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            var revisions = ordered.GroupBy(i => string.IsNullOrWhiteSpace(i.Run.HeadSha) ? null : i.Run.HeadSha,
                    StringComparer.OrdinalIgnoreCase)
                .Select(revision => new RunWorklistRevision(revision.Key,
                    revision.Max(i => i.ObservedAt),
                    revision.Key is not null && currentHead is not null
                        && string.Equals(revision.Key, currentHead, StringComparison.OrdinalIgnoreCase),
                    revision.OrderByDescending(i => i.ObservedAt).ThenByDescending(i => i.Run.RunId).ToArray()))
                .OrderByDescending(r => r.LastObservedAt).ThenBy(r => r.HeadSha, StringComparer.OrdinalIgnoreCase).ToArray();
            return new RunWorklistPrGroup(group.Key,
                ordered.Select(i => i.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                ordered.Select(i => i.RepositoryName).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                ordered.Select(i => i.BrowserUrl).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)), revisions);
        }).Where(g => !attentionOnly || g.NeedsAttention)
        .OrderByDescending(g => g.NeedsAttention).ThenByDescending(g => g.LastObservedAt)
        .ThenBy(g => g.Pr.Org, StringComparer.Ordinal).ThenBy(g => g.Pr.Project, StringComparer.Ordinal)
        .ThenBy(g => g.Pr.RepositoryId, StringComparer.Ordinal).ThenBy(g => g.Pr.PrId).ToArray();
}
