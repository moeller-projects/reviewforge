using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public enum FindingDecisionStatus { Saved, Conflict, RunNotFound, RunNotEditable, FindingNotFound, RevisionMismatch, InvalidDecision }

public sealed record FindingDecisionResult(FindingDecisionStatus Status, long CurrentVersion = 0, FindingDecision? Decision = null);

/// <summary>Run-local UI identity and its independent technical publication eligibility.
/// A human decision must never be used to override this eligibility.</summary>
public sealed record ResolvedDecisionFinding(string Id, FindingObservation? Observation, StoredFinding? AcceptedFinding)
{
    public bool PublicationEligible => FindingPayloadReader.Read(AcceptedFinding?.FindingJson) is not null &&
        (Observation is null || (Observation.Validation == FindingValidationStatus.Accepted &&
            Observation.Verification != FindingVerificationStatus.Rejected));
}

/// <summary>Validates human decisions without any PR-host writes. Actor must come from
/// authentication, e.g. dashboard-session/header-key; never accept an actor from request JSON
/// or pass a raw key, cookie or session identifier as the actor.</summary>
public sealed partial class FindingDecisionService(IFindingStore store, IFindingDecisionStore decisions, TimeProvider clock, InFlightClaims claims)
{
    /// <summary>Captured runs use observation IDs, including rejected candidates. Only legacy
    /// runs with no capture use full accepted-row dedupe keys. An empty capture is authoritative.</summary>
    public static IReadOnlyList<ResolvedDecisionFinding> ResolveFindings(ReviewRun run)
    {
        var rows = run.Findings.Where(f => f.FindingJson is not null &&
            !f.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal)).ToArray();
        if (run.FindingObservations is null)
            return rows.Select(f => new ResolvedDecisionFinding(f.DedupeKey, null, f)).ToArray();
        return run.FindingObservations.Select(o => new ResolvedDecisionFinding(o.Id.ToString("N"), o,
            o.DedupeKey is null ? null : rows.FirstOrDefault(f => string.Equals(f.DedupeKey, o.DedupeKey, StringComparison.Ordinal))))
            .ToArray();
    }

    public async Task<FindingDecisionResult> DecideAsync(Guid runId, string findingId, string headSha,
        FindingDecisionState state, string? reason, string authenticatedActor, long expectedVersion, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return new(FindingDecisionStatus.RunNotFound);
        var claimId = Guid.NewGuid();
        var pr = run.Pr;
        if (!claims.TryClaim(pr, claimId, out _)) return new(FindingDecisionStatus.Conflict);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = new ClaimHeartbeat(claims, pr, claimId, claims.Ttl / 3).RunUntilCancelled(heartbeatStop.Token);
        try
        {
            run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return new(FindingDecisionStatus.RunNotFound);
            if (run.Pipeline != RunKind.ReviewDraft.ToString() || run.CompletedAt is null || !run.Success)
                return new(FindingDecisionStatus.RunNotEditable);
            if (!string.Equals(run.HeadSha, headSha, StringComparison.Ordinal))
                return new(FindingDecisionStatus.RevisionMismatch);
            if (!ResolveFindings(run).Any(f => string.Equals(f.Id, findingId, StringComparison.Ordinal)))
                return new(FindingDecisionStatus.FindingNotFound);
            if (expectedVersion < 0 || expectedVersion == long.MaxValue)
                return new(FindingDecisionStatus.InvalidDecision);
            FindingDecision decision;
            try
            {
                decision = FindingDecision.Record(runId, findingId, headSha, state, reason, authenticatedActor,
                    clock.GetUtcNow(), expectedVersion + 1);
            }
            catch (ArgumentException)
            {
                return new(FindingDecisionStatus.InvalidDecision);
            }
            if (!claims.Renew(pr, claimId)) return new(FindingDecisionStatus.Conflict);
            if (await decisions.TryAppendFindingDecisionAsync(decision, expectedVersion, ct).ConfigureAwait(false))
                return new(FindingDecisionStatus.Saved, decision.Version, decision);
            var current = (await decisions.GetFindingDecisionsAsync(runId, ct).ConfigureAwait(false))
                .FirstOrDefault(d => string.Equals(d.FindingId, findingId, StringComparison.Ordinal));
            return new(FindingDecisionStatus.Conflict, current?.Version ?? 0, current);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            claims.Release(pr, claimId);
        }
    }
}
