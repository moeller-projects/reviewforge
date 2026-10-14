using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Core.AutoFix;

public enum AutoFixCommitGranularity
{
    PerFix,
    Single,
}

public enum AutoFixPublishMode
{
    Suggestion,
    CommitOnHead,
}

/// <summary>Configuration for the auto-fix feature (section "AutoFix"). Suggestion mode
/// is the default; with <see cref="Enabled"/> false the pipeline is byte-identical to a run
/// without the feature.</summary>
public sealed class AutoFixOptions
{
    public const string SectionName = "AutoFix";

    public bool Enabled { get; init; } = false;

    /// <summary>Immutable creator ids only — ADO display names are user-editable and never matched. Empty = disabled even when Enabled=true.</summary>
    public string[] AllowedAuthors { get; init; } = [];

    /// <summary>Rule ids eligible for auto-fix; intersected with the fixer registry.</summary>
    public string[] AllowedRuleIds { get; init; } = [];

    /// <summary>When true, every currently registered fixer is eligible regardless of <see cref="AllowedRuleIds"/>.</summary>
    public bool AllowAllRules { get; init; } = false;

    /// <summary>"Suggestion" (default) | "CommitOnHead". "StackedBranch" remains reserved.
    /// With <see cref="Enabled"/> false the pipeline is byte-identical regardless of this
    /// value — the mode is inert until the feature itself is on.</summary>
    public AutoFixPublishMode PublishMode { get; init; } = AutoFixPublishMode.Suggestion;

    /// <summary>Commit granularity in CommitOnHead mode. "PerFix" (default) = one commit per
    /// file-group: fixes touching the same file ALWAYS coalesce into that file's commit
    /// (staging is path-scoped; intra-file separation is impossible). "Single" = one commit
    /// per run. This is the contract — there is no intra-file granularity.</summary>
    public AutoFixCommitGranularity CommitGranularity { get; init; } = AutoFixCommitGranularity.PerFix;

    /// <summary>Commit identity. Required when Enabled && PublishMode=CommitOnHead; the email is
    /// also the loop-guard author reference (a discovery-triggered run on a head whose exact
    /// run trailer + this author email match is suppressed).</summary>
    public string? CommitAuthorName { get; init; } // e.g. "reviewforge[bot]"

    public string? CommitAuthorEmail { get; init; } // e.g. "reviewforge@contoso.com"

    /// <summary>True when CommitOnHead publication is active for this run's pipeline.</summary>
    public bool IsCommitOnHead
        => Enabled && PublishMode == AutoFixPublishMode.CommitOnHead;

    public const AutoFixPublishMode ModeSuggestion = AutoFixPublishMode.Suggestion;
    public const AutoFixPublishMode ModeCommitOnHead = AutoFixPublishMode.CommitOnHead;
    public const string GranularityPerFix = nameof(AutoFixCommitGranularity.PerFix);
    public const string GranularitySingle = nameof(AutoFixCommitGranularity.Single);

    /// <summary>Hard cap of applied fixes per run, shared by both fix sources.</summary>
    [Range(1, 50)]
    public int MaxFixesPerRun { get; init; } = 3;

    /// <summary>Author-commanded thread fixes: the PR author replies "/fixit" on a thread.</summary>
    public bool EnableThreadFixCommands { get; init; } = false;

    /// <summary>Iteration cap for a single fix pass (the review pass uses Review:MaxIterations).</summary>
    [Range(2, 30)]
    public int FixPassMaxIterations { get; init; } = 8;
}
