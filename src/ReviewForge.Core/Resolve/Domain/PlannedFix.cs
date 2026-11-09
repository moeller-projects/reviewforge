using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Resolve.Domain;

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
