namespace ReviewForge.Core.Pipeline;

/// <summary>Published comment identities indexed by finding dedupe key.</summary>
public sealed record PublishOutcome
{
    public IReadOnlyDictionary<string, int> PostedThreadIds { get; init; } = new Dictionary<string, int>();

    /// <summary>New threads created by this run only; excludes reconciled/reopened threads.</summary>
    public IReadOnlyDictionary<string, int> NewlyPostedThreadIds { get; init; } = new Dictionary<string, int>();
}
