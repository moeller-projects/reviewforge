using System.Diagnostics.CodeAnalysis;

namespace ReviewForge.Core.Domain;

/// <summary>Summary of the last completed run for a PR, as stored by IFindingStore.</summary>
[ExcludeFromCodeCoverage]
public sealed record PriorRun(
    PrKey Pr,
    string HeadSha,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> FindingKeys);

/// <summary>A full run record persisted by IFindingStore.</summary>
[ExcludeFromCodeCoverage]
public sealed record ReviewRun(
    Guid Id,
    PrKey Pr,
    string HeadSha,
    ReviewKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Success,
    IReadOnlyList<StoredFinding> Findings);

[ExcludeFromCodeCoverage]
public sealed record StoredFinding(
    string DedupeKey,
    string RuleId,
    string Severity,
    string Title,
    string? FilePath,
    int? Line,
    int? ThreadId);