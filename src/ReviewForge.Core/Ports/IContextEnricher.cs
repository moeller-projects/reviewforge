namespace ReviewForge.Core.Ports;

/// <summary>
/// Optional enrichment of the review context (e.g. code-review-graph over MCP).
/// Contract: fail-safe — implementations return null on any failure, never throw.
/// </summary>
public interface IContextEnricher
{
    /// <summary>Serialized enrichment payload for the agent, or null when unavailable.</summary>
    Task<string?> EnrichAsync(string repoDir, string diffText, CancellationToken ct);
}