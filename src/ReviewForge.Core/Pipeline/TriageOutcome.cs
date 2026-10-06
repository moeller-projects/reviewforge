using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline;

/// <summary>Operations chosen from the review narrative and threads requiring a reply.</summary>
public sealed record TriageOutcome
{
    public IReadOnlyList<TriageOperation> Plan { get; init; } = [];
    public IReadOnlyList<int> UnansweredThreads { get; init; } = [];
}
