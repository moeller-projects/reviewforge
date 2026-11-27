namespace ReviewForge.Core.Domain;

/// <summary>Safe, bounded execution journal. Planned stages are separate from actual executions.</summary>
public sealed record RunDiagnostics(Guid RunId, string? TraceId, IReadOnlyList<string> PlannedStages,
    IReadOnlyList<StageExecutionDiagnostic> Stages, RunContextDiagnostic Context);

public sealed record StageExecutionDiagnostic(string Stage, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    double? DurationMilliseconds, string Outcome, string? FailureCode);

/// <summary>Explicit provenance whitelist, never configuration objects, prompts or tool transcripts.</summary>
public sealed record RunContextDiagnostic(string? RuleBookVersion, string? ReviewDepth, string ModelProvenance,
    string? SourceRevision, string? TargetRevision, int? ChangedFileCount, string? ReviewSummary,
    string? VerificationSummary, string? PrSummary, IReadOnlyList<ReviewUncertainty> Uncertainties,
    IReadOnlyList<AcVerdict> AcceptanceCriteria, int? ReviewableFileCount = null, bool CollectionsTruncated = false);
