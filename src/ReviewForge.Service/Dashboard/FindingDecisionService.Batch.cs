using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

public enum BatchFindingDecisionStatus { Saved, Conflict, Invalid, NotAttempted, Unknown }
public sealed record BatchFindingDecisionSelection(string FindingId, long ExpectedVersion, FindingDecisionState State, string? Reason);
public sealed record BatchFindingDecisionRequest(string HeadSha, IReadOnlyList<BatchFindingDecisionSelection> Selections);
public sealed record BatchFindingDecisionItem(string FindingId, BatchFindingDecisionStatus Status, long? CurrentVersion = null, FindingDecisionDto? Decision = null);
/// <summary>ErrorCode is a safe, visible batch failure, not an instruction to retry. Unknown writes must be reconciled by reads.</summary>
public sealed record BatchFindingDecisionResponse(string? ErrorCode, IReadOnlyList<BatchFindingDecisionItem> Items);

public sealed partial class FindingDecisionService
{
    public const int MaxBatchSize = 500;

    /// <summary>One claim covers the whole immutable selection. Never calls the single-decision path,
    /// posts comments, or retries writes. A failed append may have committed; it stays unknown.</summary>
    public async Task<BatchFindingDecisionResponse> DecideBatchAsync(Guid runId, BatchFindingDecisionRequest request,
        string authenticatedActor, CancellationToken ct)
    {
        // Copy before the first await: callers cannot change the in-flight command.
        var selection = request.Selections?.ToArray();
        var results = selection?.Select(i => new BatchFindingDecisionItem(i?.FindingId ?? "", BatchFindingDecisionStatus.NotAttempted)).ToArray() ?? [];
        BatchFindingDecisionResponse Failure(string code) => new(code, results);
        if (string.IsNullOrWhiteSpace(request.HeadSha) || request.HeadSha.Length > 2048 || selection is null || selection.Length is < 1 or > MaxBatchSize
            || selection.Any(i => i is null || string.IsNullOrWhiteSpace(i.FindingId) || i.FindingId.Length > 2048)
            || selection.Select(i => i.FindingId).Distinct(StringComparer.Ordinal).Count() != selection.Length)
            return Failure("invalid-selection");
        if (authenticatedActor is not ("header-key" or "dashboard-session")) return Failure("invalid-actor");
        var claimId = Guid.NewGuid();
        PrKey? claimedPr = null;
        using var heartbeatStop = new CancellationTokenSource();
        Task heartbeat = Task.CompletedTask;
        try
        {
            ct.ThrowIfCancellationRequested();
            var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return Failure("run-not-found");
            if (!claims.TryClaim(run.Pr, claimId, out _)) return Failure("busy");
            claimedPr = run.Pr;
            heartbeat = new ClaimHeartbeat(claims, claimedPr, claimId, claims.Ttl / 3).RunUntilCancelled(heartbeatStop.Token);
            run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null) return Failure("run-not-found");
            if (run.Pr != claimedPr || run.Pipeline != RunKind.ReviewDraft.ToString() || run.CompletedAt is null || !run.Success)
                return Failure("run-not-editable");
            if (!string.Equals(run.HeadSha, request.HeadSha, StringComparison.Ordinal)) return Failure("revision-mismatch");
            var ids = ResolveFindings(run).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < selection.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!claims.IsHeldBy(claimedPr, claimId) || !claims.Renew(claimedPr, claimId)) return Failure("claim-lost");
                var item = selection[index];
                FindingDecision decision;
                try
                {
                    if (!ids.Contains(item.FindingId) || item.ExpectedVersion < 0 || item.ExpectedVersion == long.MaxValue)
                        throw new ArgumentException("Invalid member or version.");
                    decision = FindingDecision.Record(runId, item.FindingId, request.HeadSha, item.State, item.Reason,
                        authenticatedActor, clock.GetUtcNow(), item.ExpectedVersion + 1);
                }
                catch (ArgumentException)
                {
                    results[index] = new(item.FindingId, BatchFindingDecisionStatus.Invalid);
                    continue;
                }
                // From this point a storage exception/cancellation cannot prove that no write happened.
                results[index] = new(item.FindingId, BatchFindingDecisionStatus.Unknown);
                if (await decisions.TryAppendFindingDecisionAsync(decision, item.ExpectedVersion, ct).ConfigureAwait(false))
                    results[index] = new(item.FindingId, BatchFindingDecisionStatus.Saved, decision.Version, FindingDecisionDto.From(decision));
                else
                {
                    results[index] = new(item.FindingId, BatchFindingDecisionStatus.Conflict);
                    var current = (await decisions.GetFindingDecisionsAsync(runId, ct).ConfigureAwait(false))
                        .FirstOrDefault(d => string.Equals(d.FindingId, item.FindingId, StringComparison.Ordinal));
                    results[index] = new(item.FindingId, BatchFindingDecisionStatus.Conflict, current?.Version ?? 0,
                        current is null ? null : FindingDecisionDto.From(current));
                }
            }
            return new(null, results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Failure("cancelled"); }
        catch (Exception) { return Failure("storage-failure"); } // Explicit failed response; no hidden fallback or replay.
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            if (claimedPr is not null) claims.Release(claimedPr, claimId);
        }
    }
}
