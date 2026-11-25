using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>Fresh operational reconciliation over independently cached source metadata.
/// Read-only and advisory: submission-time claim acquisition remains authoritative.</summary>
public sealed class DashboardQueryService(DashboardInventoryCache inventory, RunTracker tracker,
    IReviewQueue queue, InFlightClaims claims, IFindingStore store, TimeProvider clock, DiscoveryOptions? discovery = null)
{
    public async Task<DashboardResponse> GetAsync(CancellationToken ct)
    {
        var cached = await inventory.GetAsync(ct).ConfigureAwait(false);
        var heads = cached.Inventory.Candidates.Select(p => new CurrentHeadQuery(p.Candidate.Key, p.Candidate.Pr.SourceCommitSha)).ToArray();
        var keys = heads.Select(h => h.Pr).ToHashSet();
        var pending = await queue.GetPendingAsync(keys, ct).ConfigureAwait(false);
        var held = claims.Snapshot();
        // Snapshot after queue read: terminal tracker state must not be resurrected by an older queue copy.
        var live = tracker.Snapshot().Where(s => keys.Contains(s.Pr)).ToArray();
        // Retained terminals are cheap state observations, not finding-bearing detail requests.
        // Exclude ALL observed IDs from persisted ranking so corrected successes cannot hide older real success.
        var selected = new HashSet<Guid>();
        var observedByPr = live.GroupBy(s => s.Pr).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var h in heads)
        {
            var observed = (observedByPr.GetValueOrDefault(h.Pr) ?? []).OrderByDescending(s => s.EnqueuedAt ?? s.StartedAt)
                .ThenByDescending(s => s.RunId).ToArray();
            var reviews = observed.Where(s => s.Kind == RunKind.Review).ToArray();
            var current = reviews.Where(s => s.HeadSha == h.HeadSha).ToArray();
            foreach (var s in new[] { reviews.FirstOrDefault(),
                current.OrderByDescending(s => s.State is RunState.Queued or RunState.Running).FirstOrDefault(),
                current.FirstOrDefault(s => s.State == RunState.Completed),
                observed.FirstOrDefault(s => s.State is RunState.Queued or RunState.Running) })
                if (s is not null) selected.Add(s.RunId);
            if (held.TryGetValue(h.Pr, out var holder)) selected.Add(holder);
        }
        var history = await store.GetRunSummariesAsync(heads, live.Select(s => s.RunId).ToArray(), selected.ToArray(), ct).ConfigureAwait(false);
        var runs = new Dictionary<Guid, (PrKey Pr, DashboardRun Run)>();
        foreach (var entry in pending)
        {
            var r = entry.Request;
            runs[r.RunId] = (r.Pr, new(r.RunId, r.HeadSha, r.Kind, entry.IsClaimed ? null : RunState.Queued,
                EnqueuedAt: r.EnqueuedAt, Trigger: r.Trigger, ProgressPercent: entry.IsClaimed ? null : 0,
                IsClaimed: entry.IsClaimed, IsPending: true) { UpdatedAt = r.EnqueuedAt });
        }
        foreach (var r in history)
        {
            var prior = runs.GetValueOrDefault(r.RunId).Run;
            var kind = r.Pipeline switch
            {
                nameof(RunKind.Review) => RunKind.Review,
                nameof(RunKind.ReviewDraft) => RunKind.ReviewDraft,
                nameof(RunKind.Resolve) => RunKind.Resolve,
                _ => (RunKind?)null,
            };
            var state = r.CompletedAt is null ? (RunState?)null : r.Success ? RunState.Completed : RunState.Failed;
            runs[r.RunId] = (r.Pr, new(r.RunId, string.IsNullOrWhiteSpace(r.HeadSha) ? null : r.HeadSha, kind, state,
                EnqueuedAt: prior?.EnqueuedAt, StartedAt: r.StartedAt, CompletedAt: r.CompletedAt,
                Trigger: prior?.Trigger, ProgressPercent: state == RunState.Completed ? 100 : null,
                ReviewFindings: r.ReviewFindings, IsClaimed: prior?.IsClaimed ?? false, IsIncomplete: r.CompletedAt is null,
                IsPending: prior?.IsPending ?? false) { UpdatedAt = r.CompletedAt ?? r.StartedAt });
        }
        // Even an unselected retained terminal must correct a pending queue copy already merged above.
        // This does not request finding details for that observation.
        foreach (var s in live.Where(s => selected.Contains(s.RunId) || runs.ContainsKey(s.RunId)))
        {
            var prior = runs.GetValueOrDefault(s.RunId).Run;
            runs[s.RunId] = (s.Pr, new(s.RunId, s.HeadSha ?? prior?.HeadSha, s.Kind, s.State,
                s.EnqueuedAt ?? prior?.EnqueuedAt, s.StartedAt ?? prior?.StartedAt, s.CompletedAt,
                s.Trigger ?? prior?.Trigger, s.CurrentStage, s.ProgressPercent, prior?.ReviewFindings,
                prior?.IsClaimed ?? false) { LoadedStages = s.LoadedStages, UpdatedAt = s.UpdatedAt, OutcomeDetail = SafeOutcome(s.State, s.Detail) });
        }
        var byPr = runs.Values.GroupBy(r => r.Pr).ToDictionary(g => g.Key, g => g.Select(r => r.Run).ToArray());
        var rows = cached.Inventory.Candidates.Select(pr =>
        {
            var candidates = byPr.GetValueOrDefault(pr.Candidate.Key) ?? [];
            var current = candidates.Where(r => r.Kind == RunKind.Review && r.HeadSha == pr.Candidate.Pr.SourceCommitSha).ToArray();
            var success = Latest(current.Where(r => r.State == RunState.Completed));
            var attempt = current.OrderByDescending(IsActive).ThenByDescending(Chronology).ThenByDescending(r => r.RunId).FirstOrDefault();
            DashboardRun? blocker;
            if (held.TryGetValue(pr.Candidate.Key, out var holder))
                blocker = candidates.FirstOrDefault(r => r.RunId == holder) ?? new DashboardRun(holder, null, null, null, IsClaimed: true);
            else
                blocker = Latest(candidates.Where(IsActive));
            var options = discovery ?? new DiscoveryOptions();
            var eligibility = DiscoveryFilter.EvaluateBasic(pr.Candidate, pr.LinkedWorkItemCount,
                success?.HeadSha, new(options.TargetBranches, options.Creators));
            return new DashboardRow(pr, success is not null, blocker is null, success, attempt, blocker, eligibility)
            {
                LatestPrAttempt = Latest(candidates.Where(r => r.Kind == RunKind.Review)),
            };
        }).ToArray();
        var summary = new DashboardSummary(rows.Length, rows.Count(IsReviewing),
            rows.Count(r => r.CurrentHeadReviewed == true), rows.Count(NeedsAttention), !cached.Inventory.IsComplete);
        return new(clock.GetUtcNow(), cached.FetchedAt, cached.Inventory.IsComplete, cached.Inventory.SourceWarnings.Concat(cached.RefreshFailed
                ? ["Inventory refresh failed; showing stale source metadata. Operational run state is fresh."] : Array.Empty<string>()).ToArray(), rows,
            summary, discovery?.SweepInterval is not null)
        {
            InventoryStale = cached.IsStale,
            InventoryRefreshing = cached.Refreshing,
            InventoryRefreshFailed = cached.RefreshFailed,
            RunStates = runs.ToDictionary(entry => entry.Key, entry => entry.Value.Run.State)
                .Concat(live.Select(s => new KeyValuePair<Guid, RunState?>(s.RunId, s.State)))
                .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value),
        };
    }

    // Exact allowlist: tracker details may contain raw exceptions, paths, or credentials.
    private static string? SafeOutcome(RunState state, string? detail) => state switch
    {
        RunState.Failed => "Review failed. See service logs for investigation.",
        RunState.Skipped => detail switch
        {
            "PR is a draft" => "Draft PR — review skipped.",
            "head already reviewed and no new human comments" => "Head already reviewed; no new human comments.",
            "bot-authored head" => "Bot-authored head — review skipped.",
            "claim lost while queued" => "Claim lost while queued.",
            _ => "Review skipped.",
        },
        _ => null,
    };

    // A terminal run may retain its claim while persistence/cleanup finishes. That blocks
    // reruns, but must not hide its outcome behind a Reviewing summary count.
    private static bool IsReviewing(DashboardRow row) => row.ActiveBlocker is { Kind: RunKind.Review } blocker
        && blocker.State is not (RunState.Completed or RunState.Skipped or RunState.Failed);
    private static bool NeedsAttention(DashboardRow row) => !IsReviewing(row) &&
        (row.LatestAttempt?.State == RunState.Failed || row.LatestPrAttempt is { HeadSha: null, State: RunState.Failed } ||
         (row.CurrentHeadReviewed != true && !row.PullRequest.Candidate.Pr.IsDraft &&
          row.Eligibility?.Status != BasicEligibilityStatus.Ineligible));

    private static bool IsActive(DashboardRun run) => run.IsPending || run.State is RunState.Queued or RunState.Running;
    private static DateTimeOffset? Chronology(DashboardRun run) => run.EnqueuedAt ?? run.StartedAt;
    private static DashboardRun? Latest(IEnumerable<DashboardRun> runs)
        => runs.OrderByDescending(Chronology).ThenByDescending(r => r.RunId).FirstOrDefault();
}
