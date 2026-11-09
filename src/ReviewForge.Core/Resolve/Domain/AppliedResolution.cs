namespace ReviewForge.Core.Resolve.Domain;

public sealed record AppliedResolution(
    int ThreadId,
    string Rationale,
    IReadOnlyList<string> Files,
    string? CommitSha = null,
    string? CommitSubject = null);
