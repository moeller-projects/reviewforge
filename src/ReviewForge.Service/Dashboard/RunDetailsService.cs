using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public sealed record DraftCapabilityResponse(bool Available, string? UnavailableReason);
public sealed record TechnicianErrorResponse(string Code);
public sealed record SetFindingDecisionRequest(string FindingId, string HeadSha, FindingDecisionState State, string? Reason, long ExpectedVersion);
public sealed record FindingDecisionDto(Guid RunId, string FindingId, string HeadSha, FindingDecisionState State,
    string? Reason, string Actor, DateTimeOffset DecidedAt, long Version)
{
    public static FindingDecisionDto From(FindingDecision d) => new(d.RunId, d.FindingId, d.HeadSha, d.State, d.Reason, d.Actor is "dashboard-session" or "header-key" ? d.Actor : "unavailable", d.DecidedAt, d.Version);
}
public sealed record FindingDecisionHistoryResponse(IReadOnlyList<FindingDecisionDto> Events);
public sealed record FindingDecisionResponse(FindingDecisionStatus Status, long CurrentVersion, FindingDecisionDto? Decision);
public sealed record FindingEvidenceDto(FindingValidationStatus Validation, string ValidationReason, string ValidationProvenance,
    FindingVerificationStatus Verification, string VerificationReason, string VerificationProvenance);
public sealed record FindingPayloadDto(string RuleId, string Title, string Severity, string Category, string Description,
    string? Snippet, string? Suggestion, FindingAnchor? Anchor, bool AnchorDowngraded);
public sealed record RunFindingDto(string FindingId, FindingPayloadDto? Candidate, FindingPayloadDto? AcceptedFinding,
    FindingEvidenceDto? Evidence, bool PreviouslyKnown, FindingDecisionDto? Decision, long DecisionVersion,
    bool? Published, int? ThreadId, bool PublicationEligible, bool CanDecide)
{
    public bool PublicationUnknown { get; init; }
}
public sealed record RunDetailsResponse(RunStatus Status, string? ReviewedHeadSha, bool ObservationsAvailable,
    FindingObservationCounts? Counts, IReadOnlyList<RunFindingDto> Findings)
{
    public RunSourceMetadata? Source { get; init; }
    public RunDiagnostics? Diagnostics { get; init; }
}

/// <summary>Explicit safe projection; no exception detail, raw persisted JSON or adapter entities cross the browser boundary.</summary>
public sealed class RunDetailsService(RunSubmissionService runs, IFindingStore store, IFindingDecisionStore decisions,
    RunSourceMetadataService? source = null)
{
    public async Task<RunDetailsResponse?> GetAsync(Guid runId, CancellationToken ct)
    {
        var status = await runs.GetStatusAsync(runId, ct).ConfigureAwait(false);
        if (status is null) return null;
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        var metadata = source is null ? null : await source.GetAsync(status.Pr, ct).ConfigureAwait(false);
        if (run is null) return new(SafeStatus(status), status.HeadSha, false, null, []) { Source = metadata };
        var latest = await decisions.GetFindingDecisionsAsync(runId, ct).ConfigureAwait(false);
        var editable = status.State == RunState.Completed && run.Success && run.CompletedAt is not null && status.Kind == RunKind.ReviewDraft;
        var findings = new List<RunFindingDto>();
        foreach (var f in FindingDecisionService.ResolveFindings(run))
        {
            var o = f.Observation;
            var row = f.AcceptedFinding;
            var decision = latest.FirstOrDefault(d => string.Equals(d.FindingId, f.Id, StringComparison.Ordinal));
            // Ordinary legacy Published=false is unknown, not proof of no external post.
            var publicationUnknown = row is not null && !row.Published && row.ThreadId is null && o?.PublicationAttempted == true;
            bool? published = row is null || publicationUnknown ? null : row.Published || row.ThreadId is not null ? true
                : run.Pipeline == nameof(RunKind.ReviewDraft) || (run.Success && run.FindingObservations is not null) ? false : null;
            findings.Add(new(f.Id, Payload(o?.FindingJson), Payload(row?.FindingJson),
                o is null ? null : new(o.Validation, o.ValidationReason, o.ValidationProvenance, o.Verification, o.VerificationReason, o.VerificationProvenance),
                o?.PreviouslyKnown ?? false, decision is null ? null : FindingDecisionDto.From(decision), decision?.Version ?? 0,
                published, row?.ThreadId, f.PublicationEligible, editable) { PublicationUnknown = publicationUnknown });
        }
        if (run.FindingObservations is null)
            foreach (var row in run.Findings.Where(f => f.FindingJson is null && !f.DedupeKey.StartsWith("thread-", StringComparison.Ordinal)))
                findings.Add(new(row.DedupeKey, null, null, null, false, null, 0,
                    row.Published || row.ThreadId is not null ? true : null, row.ThreadId, false, false));
        return new(SafeStatus(status), string.IsNullOrEmpty(run.HeadSha) ? null : run.HeadSha,
            run.FindingObservations is not null, run.FindingObservations is null ? null : FindingObservationCounts.From(run.FindingObservations), findings) { Source = metadata, Diagnostics = run.Diagnostics };
    }

    public async Task<FindingDecisionHistoryResponse?> GetHistoryAsync(Guid runId, string findingId, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || !FindingDecisionService.ResolveFindings(run).Any(f => string.Equals(f.Id, findingId, StringComparison.Ordinal))) return null;
        var history = await decisions.GetFindingDecisionHistoryAsync(runId, findingId, 100, ct).ConfigureAwait(false);
        return new(history.Select(FindingDecisionDto.From).ToArray());
    }

    public static RunStatus SafeStatus(RunStatus status) => status with
    {
        Detail = status.State switch
        {
            RunState.Failed => "internal error — see run log",
            RunState.Skipped => status.Detail switch
            {
                "PR is a draft" or "head already reviewed and no new human comments" or "bot-authored head" or "claim lost while queued" => status.Detail,
                _ => "review skipped",
            },
            _ => null,
        },
    };

    private static FindingPayloadDto? Payload(string? json)
    {
        if (json is null) return null;
        // Use the same validity boundary as publication eligibility.
        var f = FindingPayloadReader.Read(json);
        return f is null ? null : new(f.RuleId, f.Title, f.Severity, f.Category, f.Description,
            f.Snippet, f.Suggestion, f.Anchor, f.AnchorDowngraded);
    }
}
