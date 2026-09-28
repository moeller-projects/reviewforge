namespace ReviewForge.Core.Ports;

/// <summary>
/// Optional enrichment of the review context (e.g. code-review-graph over MCP).
/// Contract: fail-safe — implementations return null on any failure, never throw.
/// </summary>
public interface IContextEnricher
{
    /// <summary>Name under which the enrichment payload is stored in the review context.</summary>
    string Name => "enrichment";

    /// <summary>Serialized enrichment payload for the agent, or null when unavailable.</summary>
    Task<string?> EnrichAsync(string repoDir, string diffText, CancellationToken ct);
}