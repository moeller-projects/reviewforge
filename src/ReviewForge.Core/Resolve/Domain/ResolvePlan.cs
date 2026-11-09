namespace ReviewForge.Core.Resolve.Domain;

public sealed record ResolvePlan(
    IReadOnlyList<PlannedFix> Fixes,
    IReadOnlySet<string> WritableFiles,
    IReadOnlyList<(int ThreadId, string Reason)> Deferred);
