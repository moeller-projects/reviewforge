using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Service;

/// <summary>Review pipeline limits, prompts, output policy, and sharding settings.</summary>
public sealed class ReviewOptions
{
    public const string SectionName = "Review";

    /// <summary>Optional path to a system-prompt override file.</summary>
    public string? PromptOverridePath { get; init; }

    /// <summary>Optional directory containing rule-pack JSON overrides.</summary>
    public string? RuleSetsPath { get; init; }

    /// <summary>Maximum review context size passed to the agent.</summary>
    public int MaxContextTokens { get; init; } = 150_000;
    /// <summary>Review-agent iteration cap.</summary>
    public int MaxIterations { get; init; } = 30;

    /// <summary>Reviewer vote on clean runs: NoResponse | Approved | ApprovedWithSuggestions | None.</summary>
    public string CleanRunVote { get; init; } = "NoResponse";

    internal static bool IsValidCleanRunVote(string? value)
        => value is not null
           && (value.Equals("None", StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.NoResponse), StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.Approved), StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.ApprovedWithSuggestions), StringComparison.OrdinalIgnoreCase));

    /// <summary>Provider reasoning effort; unset keeps the provider default.</summary>
    public ReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>Enables argument-length debug breadcrumbs; the logging category must also be Debug.</summary>
    public bool AgentDebugLogging { get; init; }

    /// <summary>Total diff budget for the review prompt (~50k tokens); oversized diffs are truncated with a marker.</summary>
    public int MaxDiffChars { get; init; } = 200_000;

    /// <summary>Per-file diff budget; files over it keep their header plus a bounded prefix.</summary>
    public int MaxDiffCharsPerFile { get; init; } = 40_000;

    /// <summary>Extra diff exclusion globs; replace the default set when set (array replace, not merge).</summary>
    public string[]? DiffExcludeGlobs { get; init; }

    /// <summary>Total diff byte budget; oversized diffs are truncated with a marker.</summary>
    public long MaxDiffBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>Per-file diff byte budget.</summary>
    public int MaxDiffBytesPerFile { get; init; } = 256 * 1024;

    /// <summary>Skips the LLM call for iterations with no reviewable additions and no open threads.</summary>
    public bool TrivialDiffSkipEnabled { get; init; } = true;

    public ShardingOptions Sharding { get; init; } = new();
}

/// <summary>Map-reduce sharding for large diffs. Cap overflow falls back to the single-agent path.</summary>
public sealed class ShardingOptions
{
    public bool Enabled { get; init; }

    /// <summary>Cumulative diff chars per shard; a file larger than this gets a shard of its own.</summary>
    public int ShardMaxChars { get; init; } = 30_000;

    /// <summary>Shard cap. Overflow falls back to the legacy path, never a run failure.</summary>
    public int MaxShards { get; init; } = 8;

    /// <summary>Max concurrent shard agents (bounded by the LLM governor as well).</summary>
    public int ShardConcurrency { get; init; } = 2;
}
