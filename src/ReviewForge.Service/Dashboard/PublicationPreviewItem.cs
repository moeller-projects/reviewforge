using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Frozen readable preview; only entries without an exclusion enter the separately versioned request.</summary>
public sealed record PublicationPreviewItem(string FindingId, string Title, string Location, long Version, string? ExclusionReason)
{
    public static PublicationPreviewItem From(RunFindingDto finding, string? exclusion)
    {
        // Accepted payloads contain the validated/reanchored location actually sent to
        // the host. Candidates remain useful only when no accepted payload exists.
        var payload = finding.AcceptedFinding ?? finding.Candidate;
        var location = payload is null ? "Location unavailable"
            : payload.Anchor is { } anchor && !payload.AnchorDowngraded
                ? $"{anchor.FilePath}:{anchor.StartLine}" : "General PR comment";
        return new(finding.FindingId, payload?.Title ?? "Finding payload unavailable", location, finding.DecisionVersion, exclusion);
    }

    public static string? Exclusion(RunFindingDto finding, bool unknown) =>
        unknown || finding.PublicationUnknown || finding.Published is null ? "Unknown publication outcome — reconcile explicitly"
        : finding.Published == true ? "Already posted"
        : !finding.PublicationEligible ? "Not technically eligible"
        : finding.Decision?.State != FindingDecisionState.Approved ? "Not approved"
        : null;
}
