using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>Global durable history, independent of the current PR inventory. List projections
/// contain scalar counts only, never finding payloads. Newest StartedAt first, then ordinal run ID.
/// Attention is applied before pagination so old undecided drafts cannot be hidden by recent runs.</summary>
public interface IRunHistoryStore
{
    /// <param name="reconcileRunIds">Observed nonterminal run IDs to include even when their
    /// durable outcome no longer needs attention; allows stale live snapshots to be corrected.</param>
    Task<RunHistoryPage> GetRunHistoryAsync(int offset, int count, PrKey? pr, bool attentionOnly, CancellationToken ct,
        IReadOnlyCollection<Guid>? reconcileRunIds = null);
}

public sealed record RunHistoryItem(Guid RunId, PrKey Pr, string? HeadSha, string Pipeline,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, bool Success,
    FindingObservationCounts? Findings, int PendingDecisions, int ApprovedUnpublished)
{
    public bool NeedsAttention => CompletedAt is null || !Success ||
        (Pipeline == nameof(RunKind.ReviewDraft) && (PendingDecisions > 0 || ApprovedUnpublished > 0));
}

public sealed record RunHistoryPage(IReadOnlyList<RunHistoryItem> Items, bool HasMore);
