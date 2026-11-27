using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>Durable human decisions, separate from finding verification, dedupe identity,
/// and publication. Implementations atomically append immutable history using optimistic
/// concurrency. Services must first authorize the run and validate finding membership.</summary>
public interface IFindingDecisionStore
{
    /// <summary>Latest stored decision per finding for this run. Missing entries mean Pending,
    /// version zero, not an inferred historical decision.</summary>
    Task<IReadOnlyList<FindingDecision>> GetFindingDecisionsAsync(Guid runId, CancellationToken ct);

    /// <summary>Newest decision events first, bounded by <paramref name="count"/> (1-100).</summary>
    Task<IReadOnlyList<FindingDecision>> GetFindingDecisionHistoryAsync(
        Guid runId, string findingId, int count, CancellationToken ct);

    /// <summary>Append only when the latest version equals expectedVersion and the event has
    /// version expectedVersion + 1. A first decision expects zero. Return false for a stale
    /// version; no history is appended. Reject malformed versions rather than silently fixing
    /// them. The stored run and revision must exist and match the event.</summary>
    Task<bool> TryAppendFindingDecisionAsync(FindingDecision decision, long expectedVersion, CancellationToken ct);
}
