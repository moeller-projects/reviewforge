using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline;

/// <summary>Outputs from enrichment and reasoning, including the shared context store.</summary>
public sealed record ReasoningOutcome
{
    public ContextStore ContextStore { get; init; } = new();
    public ReviewCollector Collector { get; init; } = new();
    public ReviewResult? Result { get; init; }
}
