namespace ReviewForge.Core.Domain;

public enum RunKind
{
    Review,
    Resolve,
    ReviewDraft
}

public enum TriageVerdict
{
    Actionable,
    NonIssue,
    Question,
    AlreadyFixed,
    OutOfScope
}

public sealed record ResolvableComment(
    int ThreadId,
    ThreadAnchor? Anchor,
    string RequesterId,
    string RequesterName,
    IReadOnlyList<ThreadComment> History,
    string RequestText,
    bool CommenterAllowed);

public sealed record ThreadVerdict(
    int ThreadId,
    TriageVerdict Verdict,
    string Evidence,
    string Confidence,
    string Category = "bug",
    string? Answer = null);

public sealed record PlannedFix(
    int ThreadId,
    ThreadAnchor Anchor,
    string RequestText,
    string Evidence,
    IReadOnlyList<string> CandidateFiles,
    IReadOnlyList<int>? ClusterThreadIds = null)
{
    public IReadOnlyList<int> ThreadIds => ClusterThreadIds ?? [ThreadId];
}

public sealed record ResolvePlan(
    IReadOnlyList<PlannedFix> Fixes,
    IReadOnlySet<string> WritableFiles,
    IReadOnlyList<(int ThreadId, string Reason)> Deferred);

public sealed record AppliedResolution(
    int ThreadId,
    string Rationale,
    IReadOnlyList<string> Files,
    string? CommitSha = null,
    string? CommitSubject = null);

public enum ResolutionOutcome
{
    Fixed,
    AgentDeclined,
    NonIssue,
    Question,
    AlreadyFixed,
    OutOfScope,
    VerifyFailed,
    PushFailed,
    Deferred
}

public sealed record ResolveAction(
    int Id,
    Guid RunId,
    int ThreadId,
    TriageVerdict Verdict,
    ResolutionOutcome Outcome,
    string? CommitSha,
    bool ReplyPosted,
    DateTimeOffset CreatedAt,
    string? ReplyText = null);