using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
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
        using var sweepActivity = ReviewForgeTelemetry.Source.StartActivity("discovery.sweep");
        var sweepStart = Stopwatch.GetTimestamp();
        var candidates = await source.GetOpenPullRequestsAsync(ct);
        ReviewForgeTelemetry.DiscoveryCandidates.Add(candidates.Count);
        var enqueued = new ConcurrentQueue<PrKey>();
        var skipped = new ConcurrentQueue<SkippedPr>();
        var interesting = 0;
        var gate = new object(); // guards the cap-check/claim/enqueue critical section

        void Skip(PrKey pr, string reason)
        {
            skipped.Enqueue(new SkippedPr(pr, reason));
            ReviewForgeTelemetry.DiscoverySkipped.Add(1, new TagList { { ReviewForgeTelemetry.TagReason, NormalizeReason(reason) } });
        }

        // Phase 1 — cheap rules only (draft/branch/creator), no I/O.
        var survivors = new List<PullRequestCandidate>();
        foreach (var candidate in candidates)
        {
            var cheap = DiscoveryFilter.Evaluate(candidate, linkedWorkItemCount: 1, lastReviewedHeadSha: null, _Rules);
            if (cheap.Interesting)
            {
                survivors.Add(candidate);
            }
            else
            {
                Skip(candidate.Key, cheap.Reason);
            }
        }

        // Phase 2 — expensive round-trips fanned out, still short-circuiting cheaply first.
        await Parallel.ForEachAsync(
            survivors,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism),
                CancellationToken = ct,
            },
            async (candidate, token) =>
            {
                var workItems = await source.GetLinkedWorkItemsAsync(candidate.Key, token);
                var workItemDecision = DiscoveryFilter.Evaluate(candidate, workItems.Count, lastReviewedHeadSha: null, _Rules);
                if (!workItemDecision.Interesting)
                {
                    Skip(candidate.Key, workItemDecision.Reason);
                    return;
                }

                var prior = await store.GetLastCompletedRunAsync(candidate.Key, token);

                // Same head as the last completed run: the head check alone would skip the PR,
                // but new human comments since that run make it interesting again.
                var hasNewHumanComments = false;
                if (prior is not null
                    && string.Equals(candidate.Pr.SourceCommitSha, prior.HeadSha, StringComparison.Ordinal))
                {
                    var threads = await source.GetThreadsAsync(candidate.Key, token);
                    var watermark = prior.LastObservedCommentAt ?? prior.CompletedAt;
                    hasNewHumanComments = threads
                        .SelectMany(t => t.Comments)
                        .Any(c => !c.IsBot && c.PublishedAt > watermark);
                }

                var headDecision = DiscoveryFilter.Evaluate(
                    candidate, workItems.Count, prior?.HeadSha, _Rules, hasNewHumanComments);
                if (!headDecision.Interesting)
                {
                    Skip(candidate.Key, headDecision.Reason);
                    return;
                }

                // Failure memory: a head whose recent runs all failed backs off exponentially.
                var recentRuns = await store.GetRecentRunsAsync(candidate.Key, count: 10, token);
                var blockedUntil = FailureBackoff.BlockedUntil(
                    recentRuns, candidate.Pr.SourceCommitSha, _Clock.GetUtcNow(),
                    new FailureBackoffPolicy(options.FailureBackoffBase, options.FailureBackoffMax));
                if (blockedUntil is not null)
                {
                    Skip(candidate.Key, $"head failing; backoff until {blockedUntil.Value:u}");
                    return;
                }

                Interlocked.Increment(ref interesting);

                // Cap + claim + enqueue must be atomic relative to other candidates so the
                // per-sweep cap is exact and a failed enqueue always releases its claim.
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();

                    if (enqueued.Count >= _Rules.MaxEnqueues)
                    {
                        Skip(candidate.Key, "enqueue cap reached");
                        return;
                    }

                    var runId = Guid.NewGuid();
                    if (!claims.TryClaim(candidate.Key, runId, out _))
                    {
                        Skip(candidate.Key, "review already in flight");
                        return;
                    }

                    // Track-then-enqueue (P2-25): Queued is recorded before the channel write so a
                    // fast worker can never resurrect a finished run with a stale write.
                    tracker.Set(runId, candidate.Key, RunState.Queued);
                    var result = queue.TryEnqueue(new ReviewRequest(
                        runId, candidate.Key, _Clock.GetUtcNow(), Activity.Current?.Context));
                    if (!result.Accepted)
                    {
                        tracker.Remove(runId);
                        claims.Release(candidate.Key, runId);
                        Skip(candidate.Key, "queue full");
                        return;
                    }

                    ReviewForgeTelemetry.DiscoveryEnqueued.Add(1);
                    enqueued.Enqueue(candidate.Key);
                }
            });

        var report = new DiscoveryReport(
            candidates.Count,
            interesting,
            [.. enqueued.OrderBy(k => k.PrId)],
            [.. skipped]);
        logger?.LogInformation(
            "discovery sweep: {Candidates} candidates, {Interesting} interesting, {Enqueued} enqueued, {Skipped} skipped",
            report.Candidates, report.Interesting, report.Enqueued.Count, report.Skipped.Count);
        foreach (var skip in skipped)
        {
            logger?.LogDebug("discovery skipped {Pr}: {Reason}", skip.Pr, skip.Reason);
        }

        ReviewForgeTelemetry.DiscoverySweepDurationMilliseconds.Record(
            Stopwatch.GetElapsedTime(sweepStart).TotalMilliseconds);
        return report;
    }

    /// <summary>Maps a skip reason to a bounded, low-cardinality label for metrics.</summary>
    internal static string NormalizeReason(string reason)
    {
        if (reason == "draft")
        {
            return "draft";
        }

        if (reason.StartsWith("target branch", StringComparison.Ordinal))
        {
            return "target-branch-not-watched";
        }

        if (reason.StartsWith("creator ", StringComparison.Ordinal))
        {
            return "creator-not-allowlisted";
        }

        if (reason == "no linked work items")
        {
            return "no-linked-work-items";
        }

        if (reason == "head already reviewed")
        {
            return "head-already-reviewed";
        }

        if (reason.StartsWith("head failing; backoff", StringComparison.Ordinal))
        {
            return "head-failing-backoff";
        }

        if (reason == "enqueue cap reached")
        {
            return "enqueue-cap-reached";
        }

        if (reason == "review already in flight")
        {
            return "already-in-flight";
        }

        if (reason == "queue full")
        {
            return "queue-full";
        }

        var sb = new StringBuilder(reason.Length);
        foreach (var ch in reason)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return sb.ToString().Trim('-');
    }
}