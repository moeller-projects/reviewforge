namespace ReviewForge.Core.Domain;

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
