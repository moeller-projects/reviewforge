namespace ReviewForge.Core.Domain;

/// <summary>Cheap source metadata, independent of discovery filters and run state.</summary>
public sealed record PullRequestInventoryItem(PullRequestCandidate Candidate, string RepositoryName, string BrowserUrl,
    int? LinkedWorkItemCount = null)
{
    public IReadOnlyList<string> MetadataWarnings { get; init; } = [];
}

/// <summary>Warnings are safe, fixed messages, never raw provider errors. Incomplete does not mean empty.</summary>
public sealed record PullRequestInventory(
    IReadOnlyList<PullRequestInventoryItem> Candidates,
    bool IsComplete,
    IReadOnlyList<string> SourceWarnings);
