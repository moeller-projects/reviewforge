namespace ReviewForge.Service;

/// <summary>Git-operation concurrency and fetch behavior.</summary>
public sealed class GitOptions
{
    public const string SectionName = "Git";

    /// <summary>Dedicated concurrency limit for LibGit2Sharp operations.</summary>
    public int MaxConcurrency { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>Enables targeted/partial fetches into the shared mirror.</summary>
    public bool TargetedFetchEnabled { get; init; }
}