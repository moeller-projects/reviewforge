using System.Diagnostics;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>A review run waiting in the ingest queue.</summary>
public sealed record ReviewRequest(
    Guid RunId,
    PrKey Pr,
    DateTimeOffset EnqueuedAt,
    ActivityContext? EnqueueContext = null,
    string? HeadSha = null);

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

    /// <summary>The request when it is still live-queued (unclaimed); used for status
    /// read-through after a tracker miss.</summary>
    ReviewRequest? TryGetQueued(Guid runId);
}
