using System.Diagnostics;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;


/// <summary>How a run entered the queue. Pre-migration durable rows (null column) read as
/// <see cref="Manual"/> — safe: no code existed to create a bot-authored head before the
/// loop guard shipped, so the worst case of misclassification is one redundant review of a
/// human head.</summary>
public enum EnqueueTrigger
{
    Manual,
    Discovery,
    ResolveCommand,
}

/// <summary>A review run waiting in the ingest queue.</summary>
public sealed record ReviewRequest(
    Guid RunId,
    PrKey Pr,
    DateTimeOffset EnqueuedAt,
    ActivityContext? EnqueueContext = null,
    string? HeadSha = null,
    EnqueueTrigger Trigger = EnqueueTrigger.Manual,
    RunKind Kind = RunKind.Review);

public sealed record EnqueueResult(bool Accepted, int QueueDepth);

/// <summary>Bounded ingest queue shared by the review workers. The worker and endpoints only
/// see this port; the backing store is selected by host configuration (memory channel or
/// durable SQLite rows).</summary>
public interface IReviewQueue
{
    int Capacity { get; }

    int ApproximateDepth { get; }

    EnqueueResult TryEnqueue(ReviewRequest request);

    IAsyncEnumerable<ReviewRequest> ReadAllAsync(CancellationToken ct);

    /// <summary>Acks a consumed request. Durable implementations delete the row; the channel
    /// implementation is a no-op (the item left the channel at read time).</summary>
    void Acknowledge(Guid runId);

    /// <summary>Renews this consumer's durable claim so a long-running review is not
    /// reclaimed while still executing. The channel implementation is a no-op (in-memory
    /// claims renew via their own heartbeat). False when this consumer no longer holds the
    /// row — the caller should let the run wind down rather than renew forever.</summary>
    bool RenewClaim(Guid runId);

    /// <summary>The request when it is still live-queued (unclaimed); used for status
    /// read-through after a tracker miss.</summary>
    ReviewRequest? TryGetQueued(Guid runId);
}
