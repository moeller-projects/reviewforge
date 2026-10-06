using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline;

/// <summary>Findings accepted for publication after validation.</summary>
public sealed record ValidationOutcome
{
    public IReadOnlyList<RichFinding> AcceptedFindings { get; init; } = [];
}
