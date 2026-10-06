using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline;

/// <summary>Run classification and replies left pending from the fetched threads.</summary>
public sealed record Classification
{
    public ReviewKind Kind { get; init; } = ReviewKind.Full;
    public IReadOnlyList<PendingReply> PendingReplies { get; init; } = [];
}
