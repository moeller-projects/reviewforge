using System.ComponentModel.DataAnnotations;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Resolve.Domain;

namespace ReviewForge.Service;

public sealed class ResolveOptions
{
    public const string SectionName = "Resolve";
    public bool Enabled { get; set; }
    public string[] AllowedAuthors { get; set; } = [];

    /// <summary>Immutable commenter ids permitted to request resolve actions; empty limits requests to the PR creator.</summary>
    public string[] AllowedCommenters { get; set; } = [];

    [Range(0, 50)] public int MaxWritableFiles { get; set; } = 8;
    [Range(1, 50)] public int MaxThreadsPerRun { get; set; } = 10;
    [Range(2, 30)] public int FixPassMaxIterations { get; set; } = 8;
    public ResolveCommitGranularity CommitGranularity { get; set; } = ResolveCommitGranularity.PerThread;
    public bool SetFixedStatus { get; set; }
    public string[]? VerifyCommand { get; set; }
    [Range(1, 3600)] public int VerifyTimeoutSeconds { get; set; } = 600;
    /// <summary>Require a fresh /resolve command before collecting or discovering resolution work.</summary>
    public bool RequireCommand { get; set; }
    public bool DiscoveryEnabled { get; set; }
    [Range(1, 50)] public int TriageBatchSize { get; set; } = 20;
}
