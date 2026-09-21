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
    InFlightClaims claims,
    DiscoveryOptions options,
    ILogger<DiscoveryService>? logger = null,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly DiscoveryRules _Rules = new(options.TargetBranches, options.Creators, options.MaxEnqueuesPerSweep);

    public async Task<DiscoveryReport> RunSweepAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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

            // Same head as the last completed run: the head check alone would skip the PR,
            // but new human comments since that run (ReviewGate's condition) make it
            // interesting again — fetch threads only in this case (one extra ADO call).
            var hasNewHumanComments = false;
            if (prior is not null
                && string.Equals(candidate.Pr.SourceCommitSha, prior.HeadSha, StringComparison.Ordinal))
            {
                var threads = await source.GetThreadsAsync(candidate.Key, ct);
                hasNewHumanComments = threads
                    .SelectMany(t => t.Comments)
                    .Any(c => !c.IsBot && c.PublishedAt > prior.CompletedAt);
            }

            var headDecision = DiscoveryFilter.Evaluate(
                candidate, workItems.Count, prior?.HeadSha, _Rules, hasNewHumanComments);
            if (!headDecision.Interesting)
            {
                skipped.Add(new SkippedPr(candidate.Key, headDecision.Reason));
                continue;
            }

            // Failure memory: a head whose recent runs all failed backs off exponentially
            // instead of burning an LLM run on every sweep.
            var recentRuns = await store.GetRecentRunsAsync(candidate.Key, count: 10, ct);
            var blockedUntil = FailureBackoff.BlockedUntil(
                recentRuns, candidate.Pr.SourceCommitSha, _Clock.GetUtcNow(),
                new FailureBackoffPolicy(options.FailureBackoffBase, options.FailureBackoffMax));
            if (blockedUntil is not null)
            {
                skipped.Add(new SkippedPr(candidate.Key, $"head failing; backoff until {blockedUntil.Value:u}"));
                continue;
            }

            interesting++;
            if (enqueued.Count >= _Rules.MaxEnqueues)
            {
                skipped.Add(new SkippedPr(candidate.Key, "enqueue cap reached"));
                continue;
            }

            // The final store read above is cancellable; a request cancelled during it must
            // not still reserve and enqueue a review. Re-check right before claiming.
            ct.ThrowIfCancellationRequested();

            var runId = Guid.NewGuid();
            if (!claims.TryClaim(candidate.Key, runId, out _))
            {
                skipped.Add(new SkippedPr(candidate.Key, "review already in flight"));
                continue;
            }

            var result = queue.TryEnqueue(new ReviewRequest(runId, candidate.Key, _Clock.GetUtcNow()));
            if (!result.Accepted)
            {
                claims.Release(candidate.Key, runId);
                skipped.Add(new SkippedPr(candidate.Key, "queue full"));
                continue;
            }

            tracker.Set(runId, candidate.Key, RunState.Queued);
            enqueued.Add(candidate.Key);
        }

        var report = new DiscoveryReport(candidates.Count, interesting, enqueued, skipped);
        logger?.LogInformation(
            "discovery sweep: {Candidates} candidates, {Interesting} interesting, {Enqueued} enqueued, {Skipped} skipped",
            report.Candidates, report.Interesting, report.Enqueued.Count, report.Skipped.Count);
        foreach (var skip in skipped)
        {
            logger?.LogDebug("discovery skipped {Pr}: {Reason}", skip.Pr, skip.Reason);
        }

        return report;
    }
}