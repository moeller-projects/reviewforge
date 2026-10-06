namespace ReviewForge.Service;

/// <summary>Git-operation concurrency and fetch behavior.</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Dedicated concurrency limit for LibGit2Sharp operations.</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>Enables targeted/partial fetches into the shared mirror.</summary>
    public bool TargetedFetchEnabled { get; init; }
}