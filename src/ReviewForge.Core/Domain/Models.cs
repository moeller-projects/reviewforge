using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace ReviewForge.Core.Domain;

/// <summary>Stable identity of a pull request across runs; key for the finding store.</summary>
public sealed record PrKey(string Org, string Project, string RepositoryId, int PrId)
{
    public override string ToString() => $"{Org}/{Project}/{RepositoryId}/{PrId}";
}

[ExcludeFromCodeCoverage]
public sealed record PullRequest(
    int Id,
    string Title,
    string? Description,
    string SourceCommitSha,
    string TargetCommitSha,
    string CloneUrl,
    bool IsDraft);

[ExcludeFromCodeCoverage]
public sealed record WorkItem(
    int Id,
    string Title,
    string Type,
    string? Description,
    string? AcceptanceCriteria,
    string State);

[ExcludeFromCodeCoverage]
public sealed record CurrentUser(string Id, string DisplayName);

public enum ChangedFileType
{
    Add,
    Edit,
    Delete,
    Rename,
    Unknown
}

/// <summary>Provider-authoritative file scope for the current pull-request iteration.</summary>
[ExcludeFromCodeCoverage]
public sealed record ChangedFile(string Path, ChangedFileType ChangeType);

public enum ReviewThreadStatus
{
    Unknown,
    Active,
    Fixed,
    Closed,
    Pending
}

[ExcludeFromCodeCoverage]
public sealed record ThreadComment(string AuthorId, string AuthorName, bool IsBot, string Text, DateTimeOffset PublishedAt);

public sealed record ReviewThread(
    int Id,
    string? DedupeKey,
    ReviewThreadStatus Status,
    IReadOnlyList<ThreadComment> Comments)
{
    public ThreadComment? LastComment => Comments.Count == 0 ? null : Comments[^1];
    public bool HasPendingHumanReply => LastComment is {IsBot: false};
}

/// <summary>Location of a finding in the PR diff.</summary>
[ExcludeFromCodeCoverage]
public sealed record FindingAnchor(
    [property: Required] string FilePath,
    [property: Range(1, int.MaxValue)] int StartLine,
    [property: Range(1, int.MaxValue)] int EndLine);

/// <summary>One validated review finding, recorded via the record_finding tool.</summary>
public sealed record RichFinding
{
    [Required] public required string RuleId { get; init; }
    [Required] public required string Title { get; init; }

    [Required, AllowedValues("critical", "high", "medium", "low", "info")]
    public required string Severity { get; init; }

    [Required, AllowedValues("bug", "security", "performance", "style", "docs")]
    public required string Category { get; init; }

    [Required] public required string Description { get; init; }

    [MaxLength(2000)] public string? Snippet { get; init; }
    public string? Suggestion { get; init; }

    // Set/corrected by the pipeline, not the model:
    public FindingAnchor? Anchor { get; set; }
    public string? DedupeKey { get; set; }
    public bool AnchorDowngraded { get; set; }
}

[ExcludeFromCodeCoverage]
public sealed record ReviewUncertainty(string Topic, string Question, string? Context);

public enum AcStatus
{
    Met,
    Unmet,
    Unclear
}

/// <summary>Verdict on one acceptance criterion of a linked work item.</summary>
[ExcludeFromCodeCoverage]
public sealed record AcVerdict(
    [property: Required] int WorkItemId,
    [property: Required] string Criterion,
    [property: Required] AcStatus Status,
    string? Evidence);

public enum ThreadActionKind
{
    Answer,
    Resolve,
    Reopen
}

/// <summary>Agent-decided action on an existing PR thread.</summary>
[ExcludeFromCodeCoverage]
public sealed record ThreadAction(
    [property: Required] int ThreadId,
    [property: Required] ThreadActionKind Action,
    [property: Required] string Comment);

/// <summary>Final narrative, carried by the task_done tool call.</summary>
public sealed record ReviewNarrative
{
    public string? ReviewSummary { get; init; }
    public string? VerificationSummary { get; init; }
    public string? PrSummary { get; init; }
    public string[]? GoodPractices { get; init; }
    public AcVerdict[]? AcceptanceCriteria { get; init; }
    public ThreadAction[]? ThreadActions { get; init; }
}

public sealed record ReviewResult
{
    public required ReviewNarrative Narrative { get; init; }
    public required IReadOnlyList<RichFinding> Findings { get; init; }
    public required IReadOnlyList<ReviewUncertainty> Uncertainties { get; init; }
    public string ReviewDepth { get; set; } = "agentic tool loop";
    public string? RuleBookVersion { get; init; }
}

public enum ReviewKind
{
    Full,
    FollowUp
}

/// <summary>A human reply on a bot thread that has not been answered yet.</summary>
[ExcludeFromCodeCoverage]
public sealed record PendingReply(int ThreadId, string? DedupeKey, string Author, string Text);