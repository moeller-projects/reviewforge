using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>A candidate that was skipped, with the reason.</summary>
public sealed record SkippedPr(PrKey Pr, string Reason);

/// <summary>Result of one org-wide discovery sweep.</summary>
public sealed record DiscoveryReport(
    int Candidates,
    int Interesting,
    IReadOnlyList<PrKey> Enqueued,
    IReadOnlyList<SkippedPr> Skipped);

/// <summary>
/// Runs an org-wide sweep: fetches active PRs, filters them, and enqueues the interesting
/// ones (up to the per-sweep cap) using the same queue/tracker path as the submit endpoint.
/// </summary>
public sealed class DiscoveryService(
    IPullRequestSource source,
    IFindingStore store,
    ReviewQueue queue,
    RunTracker tracker,
    DiscoveryOptions options,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly DiscoveryRules _Rules = new(options.TargetBranches, options.Creators, options.MaxEnqueuesPerSweep);

    public async Task<DiscoveryReport> RunSweepAsync(CancellationToken ct)
    {
        var candidates = await source.GetOpenPullRequestsAsync(ct);
        var enqueued = new List<PrKey>();
        var skipped = new List<SkippedPr>();
        var interesting = 0;

        foreach (var candidate in candidates)
        {
            // Cheap rules (draft/branch/creator) first — avoids the work-item + store round trips.
            var cheap = DiscoveryFilter.Evaluate(candidate, linkedWorkItemCount: 1, lastReviewedHeadSha: null, _Rules);
            if (!cheap.Interesting)
            {
                skipped.Add(new SkippedPr(candidate.Key, cheap.Reason));
                continue;
            }

            var workItems = await source.GetLinkedWorkItemsAsync(candidate.Key, ct);
            var workItemDecision = DiscoveryFilter.Evaluate(candidate, workItems.Count, lastReviewedHeadSha: null, _Rules);
            if (!workItemDecision.Interesting)
            {
                skipped.Add(new SkippedPr(candidate.Key, workItemDecision.Reason));
                continue;
            }

            var prior = await store.GetLastCompletedRunAsync(candidate.Key, ct);
            var headDecision = DiscoveryFilter.Evaluate(candidate, workItems.Count, prior?.HeadSha, _Rules);
            if (!headDecision.Interesting)
            {
                skipped.Add(new SkippedPr(candidate.Key, headDecision.Reason));
                continue;
            }

            interesting++;
            if (enqueued.Count >= _Rules.MaxEnqueues)
            {
                skipped.Add(new SkippedPr(candidate.Key, "enqueue cap reached"));
                continue;
            }

            var runId = Guid.NewGuid();
            await queue.EnqueueAsync(new ReviewRequest(runId, candidate.Key, _Clock.GetUtcNow()), ct);
            tracker.Set(runId, candidate.Key, RunState.Queued);
            enqueued.Add(candidate.Key);
        }

        return new DiscoveryReport(candidates.Count, interesting, enqueued, skipped);
    }
}