using System.Text.Json;

namespace ReviewForge.Core.Domain;

public enum FindingValidationStatus { NotRun, Accepted, Rejected }
public enum FindingVerificationStatus { Unverified, Confirmed, Rejected, Deterministic, Failed }

/// <summary>A recorded candidate in ONE run, never a cross-run dedupe identity. Payload is
/// captured before reanchoring or fixing. Verification is a lightweight challenge, not confidence.
/// Invalid tool calls which never reached the collector are not observations.</summary>
public sealed record FindingObservation(
    Guid Id,
    string? DedupeKey,
    string FindingJson,
    bool PreviouslyKnown,
    FindingValidationStatus Validation,
    string ValidationReason,
    string ValidationProvenance,
    FindingVerificationStatus Verification,
    string VerificationReason,
    string VerificationProvenance)
{
    /// <summary>True only for a new finding thread posted during this run, not reconciliation/reopening.</summary>
    public bool NewlyPosted { get; init; }

    /// <summary>A durable pre-write intent. Until a thread is confirmed in the publication
    /// audit, the outcome is Unknown rather than definitely Unpublished.</summary>
    public bool PublicationAttempted { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static FindingObservation Create(RichFinding finding, bool previouslyKnown = false)
    {
        var deterministic = finding.RuleId.StartsWith("homoglyph/", StringComparison.Ordinal);
        return new(
            Guid.NewGuid(), finding.DedupeKey, JsonSerializer.Serialize(finding, JsonOptions), previouslyKnown,
            FindingValidationStatus.NotRun, previouslyKnown ? "previously-known" : "not-run", "collector",
            deterministic ? FindingVerificationStatus.Deterministic : FindingVerificationStatus.Unverified,
            deterministic ? "deterministic-rule" : previouslyKnown ? "previously-known" : "not-run",
            deterministic ? "homoglyph-analyzer" : "none");
    }
}

/// <summary>Current-run counts only. Carried-forward identities are intentionally absent.
/// Accepted includes fail-open/unverified candidates, not only confirmed candidates.
/// Null observations mean unavailable history, not zero discoveries.</summary>
public sealed record FindingObservationCounts(int Candidates, int Accepted, int Rejected, int PreviouslyKnown, int NewlyPosted)
{
    public static FindingObservationCounts From(IReadOnlyList<FindingObservation> observations) => new(
        observations.Count,
        observations.Count(o => !o.PreviouslyKnown && o.Validation == FindingValidationStatus.Accepted
            && o.Verification != FindingVerificationStatus.Rejected),
        observations.Count(o => o.Validation == FindingValidationStatus.Rejected || o.Verification == FindingVerificationStatus.Rejected),
        observations.Count(o => o.PreviouslyKnown),
        observations.Where(o => o.NewlyPosted).Select(o => o.DedupeKey).Distinct(StringComparer.Ordinal).Count());
}
