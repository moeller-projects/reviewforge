namespace ReviewForge.Service;

/// <summary>Context enrichment settings. Every enricher is opt-in.</summary>
public sealed class EnrichmentOptions
{
    public const string SectionName = "Enrichment";

    /// <summary>Precomputes a map of references to symbols touched by the pull request.</summary>
    public bool SymbolUsageEnabled { get; init; }
}
