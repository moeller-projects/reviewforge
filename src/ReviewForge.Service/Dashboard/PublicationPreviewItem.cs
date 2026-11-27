using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Frozen readable preview; only entries without an exclusion enter the separately versioned request.</summary>
public sealed record PublicationPreviewItem(string FindingId, string Title, string Location, long Version, string? ExclusionReason)
{
    public static PublicationPreviewItem From(RunFindingDto finding, string? exclusion)
    {
        var payload = FindingWorkspaceState.Payload(finding);
        var location = payload?.Anchor is { } anchor ? $"{anchor.FilePath}:{anchor.StartLine}" : "Location unavailable";
        return new(finding.FindingId, payload?.Title ?? "Finding payload unavailable", location, finding.DecisionVersion, exclusion);
    }

    public static string? Exclusion(RunFindingDto finding, bool unknown) =>
        unknown || finding.PublicationUnknown || finding.Published is null ? "Unknown publication outcome — reconcile explicitly"
        : finding.Published == true ? "Already posted"
        : !finding.PublicationEligible ? "Not technically eligible"
        : finding.Decision?.State != FindingDecisionState.Approved ? "Not approved"
        : null;
}
