using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public sealed record RunWorklistResponse(IReadOnlyList<RunWorklistItem> Items, bool HasMore,
    int Offset, int Count, DateTimeOffset GeneratedAt, DateTimeOffset InventoryFetchedAt,
    bool InventoryStale, bool InventoryComplete, IReadOnlyList<string> SourceWarnings);

/// <summary>Durable global history with bounded operational overlays. Source inventory enriches
/// rows only; missing/closed PR metadata never removes a run. Offset is capped at 10,000 to bound
/// reconciliation work; each database page contains at most 100 scalar summaries.</summary>
public sealed class RunWorklistQueryService(DashboardInventoryCache inventory, RunTracker tracker,
    IReviewQueue queue, InFlightClaims claims, IRunHistoryStore store, TimeProvider clock)
{
    public async Task<RunWorklistResponse> GetAsync(int offset, int count, PrKey? pr, bool attentionOnly, CancellationToken ct)
    {
        if (offset is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        CachedInventory cached;
        try { cached = await inventory.GetAsync(ct).ConfigureAwait(false); }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not ObjectDisposedException)
        {
            // Metadata is optional enrichment, not the history engine. Surface its absence
            // explicitly; the durable-store read below is deliberately outside this catch.
            cached = new(new([], false, ["Source metadata unavailable; showing durable run identities."]), clock.GetUtcNow())
                { IsStale = true, RefreshFailed = true };
        }
        var pending = await queue.GetPendingHistoryAsync(1000, ct).ConfigureAwait(false);
        var scopedPending = pending.Where(p => pr is null || p.Request.Pr == pr).ToArray();
        var held = claims.Snapshot();
        // Snapshot AFTER queue read: retained terminal corrections override stale durable claims.
        var live = tracker.Snapshot().Where(s => pr is null || s.Pr == pr).ToDictionary(s => s.RunId);
        // A restart can lose the tracker after persistence finalizes but before queue acknowledgement.
        // Include bounded pending identities so SQL returns terminal corrections even in attention mode.
        var reconcileIds = live.Values.Where(s => s.State is RunState.Queued or RunState.Running)
            .Select(s => s.RunId).Union(scopedPending.Select(p => p.Request.RunId)).ToArray();
        var rows = new Dictionary<Guid, RunWorklistItem>();
        foreach (var p in scopedPending)
        {
            var r = p.Request;
            rows[r.RunId] = new(r.Pr, new(r.RunId, r.HeadSha, r.Kind, p.IsClaimed ? null : RunState.Queued,
                EnqueuedAt: r.EnqueuedAt, Trigger: r.Trigger, ProgressPercent: p.IsClaimed ? null : 0,
                IsClaimed: p.IsClaimed || held.GetValueOrDefault(r.Pr) == r.RunId, IsPending: true) { UpdatedAt = r.EnqueuedAt });
        }
        // Claims can belong to decision/publication operations, not review runs. Only
        // queue/tracker/store observations establish run identities; unmatched claims
        // remain PR blockers in the submission boundary and inventory projection.

        void Overlay()
        {
            foreach (var s in live.Values)
            {
                rows.TryGetValue(s.RunId, out var prior);
                // Persistence can finalize before the worker updates its tracker. A stale
                // nonterminal snapshot must not erase a newer durable terminal result.
                if (prior?.Run.State is RunState.Completed or RunState.Failed or RunState.Skipped
                    && s.State is RunState.Queued or RunState.Running)
                    continue;
                rows[s.RunId] = new(s.Pr, new(s.RunId, s.HeadSha ?? prior?.Run.HeadSha, s.Kind, s.State,
                    s.EnqueuedAt ?? prior?.Run.EnqueuedAt, s.StartedAt ?? prior?.Run.StartedAt, s.CompletedAt,
                    s.Trigger ?? prior?.Run.Trigger, s.CurrentStage, s.ProgressPercent, prior?.Run.ReviewFindings,
                    IsClaimed: held.GetValueOrDefault(s.Pr) == s.RunId)
                {
                    UpdatedAt = s.UpdatedAt, LoadedStages = s.LoadedStages,
                    OutcomeDetail = RunDetailsService.SafeStatus(s).Detail,
                }, Findings: prior?.Findings, PendingDecisions: prior?.PendingDecisions, ApprovedUnpublished: prior?.ApprovedUnpublished);
            }
        }
        RunWorklistItem[] Ordered() => rows.Values.Where(i => !attentionOnly || i.NeedsAttention)
            .OrderByDescending(i => i.ObservedAt).ThenByDescending(i => i.Run.RunId.ToString("D"), StringComparer.Ordinal).ToArray();

        // Start at the beginning so inserted live runs and terminal corrections cannot create
        // duplicate/gapped pages. Attention is filtered IN SQL, not in an arbitrary recent window.
        var durableOffset = 0;
        bool durableHasMore;
        RunWorklistItem[] ordered;
        var prefixComplete = false;
        do
        {
            var page = await store.GetRunHistoryAsync(durableOffset, 100, pr, attentionOnly, ct, reconcileIds).ConfigureAwait(false);
            foreach (var r in page.Items)
            {
                rows.TryGetValue(r.RunId, out var prior);
                var kind = Enum.TryParse<RunKind>(r.Pipeline, out var parsed) && Enum.IsDefined(parsed) ? parsed : (RunKind?)null;
                var state = r.CompletedAt is null ? (RunState?)null : r.Success ? RunState.Completed : RunState.Failed;
                rows[r.RunId] = new(r.Pr, new(r.RunId, r.HeadSha, kind, state,
                    EnqueuedAt: prior?.Run.EnqueuedAt, StartedAt: r.StartedAt, CompletedAt: r.CompletedAt,
                    Trigger: prior?.Run.Trigger, ProgressPercent: state == RunState.Completed ? 100 : null,
                    IsClaimed: held.GetValueOrDefault(r.Pr) == r.RunId, IsIncomplete: r.CompletedAt is null,
                    IsPending: state is null && (prior?.Run.IsPending ?? false)) { UpdatedAt = r.CompletedAt ?? r.StartedAt },
                    Findings: r.Findings, PendingDecisions: r.PendingDecisions, ApprovedUnpublished: r.ApprovedUnpublished);
            }
            durableOffset += page.Items.Count;
            durableHasMore = page.HasMore;
            Overlay();
            ordered = Ordered();
            // A tracker may move chronology backwards (enqueue precedes durable start).
            // Continue until even the lookahead sorts ahead of the unread durable tail.
            if (ordered.Length > offset + count && page.Items.Count > 0)
            {
                var boundary = ordered[offset + count];
                var tail = page.Items[^1];
                prefixComplete = boundary.ObservedAt > tail.StartedAt ||
                    (boundary.ObservedAt == tail.StartedAt && string.CompareOrdinal(
                        boundary.Run.RunId.ToString("D"), tail.RunId.ToString("D")) >= 0);
            }
        } while (durableHasMore && !prefixComplete);

        var metadata = cached.Inventory.Candidates.GroupBy(p => p.Candidate.Key).ToDictionary(g => g.Key, g => g.First());
        var items = ordered.Skip(offset).Take(count).Select(i => metadata.TryGetValue(i.Pr, out var p)
            ? i with { Title = p.Candidate.Pr.Title, RepositoryName = p.RepositoryName, BrowserUrl = p.BrowserUrl, CurrentHeadSha = p.Candidate.Pr.SourceCommitSha }
            : i).ToArray();
        var warnings = cached.Inventory.SourceWarnings.ToList();
        if (cached.RefreshFailed) warnings.Add("Inventory refresh failed; showing stale source metadata.");
        if (pending.Count == 1000) warnings.Add("Pending queue snapshot reached its bound; additional queued runs may be omitted.");
        return new(items, durableHasMore || ordered.Length > offset + count, offset, count, clock.GetUtcNow(),
            cached.FetchedAt, cached.IsStale, cached.Inventory.IsComplete, warnings);
    }
}
