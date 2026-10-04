using System.ComponentModel.DataAnnotations;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Service;

/// <summary>Agent repo-scan budgets for one Grep tool call.</summary>
public sealed class RepoReadToolsOptions
{
    public const string SectionName = "RepoReadTools";

    /// <summary>Aggregate wall-clock budget (ms) for one Grep call; default 10 s.</summary>
    [Range(1, 600_000)]
    public int GrepMaxMs { get; init; } = RepoReadTools.DefaultGrepMaxMs;

    /// <summary>Aggregate line budget for one Grep call; default 200k lines.</summary>
    [Range(1, 10_000_000)]
    public int GrepMaxLines { get; init; } = RepoReadTools.DefaultGrepMaxLines;
}
