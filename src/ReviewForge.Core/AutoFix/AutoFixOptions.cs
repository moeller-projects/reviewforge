using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Core.AutoFix;

/// <summary>Configuration for the suggestion-only auto-fix feature (section "AutoFix").
/// Everything is off by default: with <see cref="Enabled"/> false the pipeline is
/// byte-identical to a run without the feature.</summary>
public sealed class AutoFixOptions
{
    public const string SectionName = "AutoFix";

    public bool Enabled { get; init; } = false;

    /// <summary>Creator ids or display names. Empty = disabled even when Enabled=true.</summary>
    public string[] AllowedAuthors { get; init; } = [];

    /// <summary>Rule ids eligible for auto-fix; intersected with the fixer registry.</summary>
    public string[] AllowedRuleIds { get; init; } = [];

    /// <summary>Only "Suggestion" is supported in this version; other values fail startup.
    /// CommitOnHead / StackedBranch are reserved for the deferred write modes.</summary>
    public string PublishMode { get; init; } = "Suggestion";

    /// <summary>Hard cap of applied fixes per run, shared by both fix sources.</summary>
    [Range(1, 50)] public int MaxFixesPerRun { get; init; } = 3;

    /// <summary>Optional command run in the checkout to verify fixes. Requires workspace writes.</summary>
    public string? VerificationCommand { get; init; }

    [Range(5, 1800)] public int VerificationTimeoutSeconds { get; init; } = 120;

    /// <summary>Author-commanded thread fixes: the PR author replies "/rf fix" on a thread.</summary>
    public bool EnableThreadFixCommands { get; init; } = false;

    /// <summary>Iteration cap for a single fix pass (the review pass uses ReviewForge:MaxIterations).</summary>
    [Range(2, 30)] public int FixPassMaxIterations { get; init; } = 8;
}
