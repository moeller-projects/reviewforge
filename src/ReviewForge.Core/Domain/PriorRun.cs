using System.Diagnostics.CodeAnalysis;

namespace ReviewForge.Core.Domain;

/// <summary>Summary of the last completed run for a PR, as stored by IFindingStore.</summary>
[ExcludeFromCodeCoverage]
public sealed record PriorRun(
    PrKey Pr,
    string HeadSha,
    DateTimeOffset CompletedAt,
    IReadOnlyList<string> FindingKeys,
    /// <summary>Full finding rows of that run; lets the next run carry them forward so the
    /// known-key set does not decay to accepted-findings-only. Null when loaded from a
    /// source that only has keys.</summary>
    IReadOnlyList<StoredFinding>? Findings = null,
    /// <summary>Newest comment timestamp (ADO server time) the run observed at fetch; the
    /// gate compares future comment timestamps against this instead of the local-clock
    /// CompletedAt. Null for runs persisted before the watermark existed (gate falls back).</summary>
    DateTimeOffset? LastObservedCommentAt = null);

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
    IReadOnlyList<StoredFinding> Findings,
    DateTimeOffset? LastObservedCommentAt = null);

[ExcludeFromCodeCoverage]
public sealed record StoredFinding(
    string DedupeKey,
    string RuleId,
    string Severity,
    string Title,
    string? FilePath,
    int? Line,
    int? ThreadId);