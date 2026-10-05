using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
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
    IReviewQueue queue,
    RunTracker tracker,
    InFlightClaims claims,
    DiscoveryOptions options,
    RetentionOptions retention,
    RepoCheckoutPool? pool = null,
    ILogger<DiscoveryService>? logger = null,
    TimeProvider? clock = null,
    AutoFixOptions? autoFix = null,
    ResolveOptions? resolveOptions = null)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly DiscoveryRules _Rules = new(options.TargetBranches, options.Creators, options.MaxEnqueuesPerSweep);
    private readonly RepoCheckoutPool? _Pool = pool;
    private readonly ResolveOptions? _Resolve = resolveOptions;

    private readonly SemaphoreSlim? _WarmupGate =
        pool is not null && options.WarmupEnabled ? new SemaphoreSlim(Math.Max(1, options.WarmupConcurrency)) : null;

    private DateTimeOffset _LastPrune = DateTimeOffset.MinValue; // sweep-throttled (P2-26)

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
        var warmups = _WarmupGate is null ? (List<Task>?) null : []; // mirror prefetches, awaited before the sweep returns

        void Skip(PrKey pr, string reason)
        {
            skipped.Enqueue(new SkippedPr(pr, reason));
            ReviewForgeTelemetry.DiscoverySkipped.Add(1, new TagList {{ReviewForgeTelemetry.TagReason, NormalizeReason(reason)}});
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
                try
                {
                    await EvaluateCandidateAsync(candidate, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Sweeps are best-effort discovery: one faulting candidate becomes a
                    // skip, never a sweep failure — the per-run gate remains the net.
                    ReviewForgeTelemetry.DiscoveryCandidateErrors.Add(1);
                    logger?.LogWarning(ex, "discovery candidate {Pr} faulted; isolated as a skip", candidate.Key);
                    Skip(candidate.Key, $"error: {ex.GetType().Name}");
                }
            });

        // Report assembly is deterministic — completion order never leaks into the report.
        var report = new DiscoveryReport(
            candidates.Count,
            interesting,
            [
                .. enqueued.OrderBy(k => k.Org, StringComparer.Ordinal)
                    .ThenBy(k => k.Project, StringComparer.Ordinal)
                    .ThenBy(k => k.RepositoryId, StringComparer.Ordinal)
                    .ThenBy(k => k.PrId)
            ],
            [
                .. skipped.OrderBy(s => s.Pr.Org, StringComparer.Ordinal)
                    .ThenBy(s => s.Pr.Project, StringComparer.Ordinal)
                    .ThenBy(s => s.Pr.RepositoryId, StringComparer.Ordinal)
                    .ThenBy(s => s.Pr.PrId)
            ]);
        logger?.LogInformation(
            "discovery sweep: {Candidates} candidates, {Interesting} interesting, {Enqueued} enqueued, {Skipped} skipped",
            report.Candidates, report.Interesting, report.Enqueued.Count, report.Skipped.Count);
        foreach (var skip in skipped)
        {
            logger?.LogDebug("discovery skipped {Pr}: {Reason}", skip.Pr, skip.Reason);
        }

        // Warmups are part of the sweep: the span and the sweep-duration metric cover their
        // outcomes. Individual failures are already swallowed into skip-free telemetry above.
        if (warmups is {Count: > 0})
        {
            await Task.WhenAll(warmups).ConfigureAwait(false);
        }

        ReviewForgeTelemetry.DiscoverySweepDurationMilliseconds.Record(
            Stopwatch.GetElapsedTime(sweepStart).TotalMilliseconds);

        // Retention tail (P2-26): prune the store at most once per hour, after the sweep's
        // own work is done so prune cost never delays candidate processing.
        var now = _Clock.GetUtcNow();
        if (now - _LastPrune >= TimeSpan.FromHours(1))
        {
            _LastPrune = now;
            var pruned = await store.PruneAsync(now.AddDays(-retention.Days), retention.MinRunsPerPr, ct);
            if (pruned > 0)
            {
                logger?.LogInformation(
                    "retention pruned {Pruned} runs older than {RetentionDays} days (kept last {MinRunsPerPr} runs per PR)",
                    pruned, retention.Days, retention.MinRunsPerPr);
            }
        }

        return report;

        async Task EvaluateCandidateAsync(PullRequestCandidate candidate, CancellationToken token)
        {
            IReadOnlyList<ReviewThread>? threads = null;
            ResolveCommand? resolveCommand = null;
            var resolveRun = false;
            if (_Resolve is {Enabled: true} && !candidate.Pr.IsDraft
                                            && _Resolve.AllowedAuthors.Contains(candidate.Pr.CreatorId, StringComparer.OrdinalIgnoreCase))
            {
                var previousResolve = await store.GetLastCompletedResolveRunAsync(candidate.Key, token);
                var resolveWatermark = previousResolve?.LastObservedCommentAt ?? previousResolve?.CompletedAt;
                threads = await source.GetThreadsAsync(candidate.Key, token);
                resolveCommand = ResolveCommandDetector.Scan(threads, candidate.Pr.CreatorId, resolveWatermark)
                    .OrderBy(c => c.PublishedAt).LastOrDefault();
                var newHumanComment = threads.SelectMany(t => t.Comments)
                    .Any(c => !c.IsBot && (resolveWatermark is null || c.PublishedAt > resolveWatermark));
                var deferredThreadIds = (await store.GetResolveActionsAsync(candidate.Key, [], token)
                        .ConfigureAwait(false))
                    .Where(action => action.Outcome == ResolutionOutcome.Deferred)
                    .Select(action => action.ThreadId).ToHashSet();
                var hasDeferredActions = threads.Any(thread =>
                    deferredThreadIds.Contains(thread.Id)
                    && thread.Status is not (ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                    && thread.Comments.Any(comment => !comment.IsBot));
                resolveRun = resolveCommand is not null || (_Resolve.DiscoveryEnabled && (newHumanComment || hasDeferredActions));
            }

            var workItems = await source.GetLinkedWorkItemsAsync(candidate.Key, token);
            var workItemDecision = DiscoveryFilter.Evaluate(candidate, workItems.Count, lastReviewedHeadSha: null, _Rules);
            if (!workItemDecision.Interesting && !resolveRun)
            {
                Skip(candidate.Key, workItemDecision.Reason);
                return;
            }

            var prior = await store.GetLastCompletedRunAsync(candidate.Key, token);
            var hasNewHumanComments = false;
            if (prior is not null
                && string.Equals(candidate.Pr.SourceCommitSha, prior.HeadSha, StringComparison.Ordinal))
            {
                threads ??= await source.GetThreadsAsync(candidate.Key, token);
                var watermark = prior.LastObservedCommentAt ?? prior.CompletedAt;
                hasNewHumanComments = threads
                    .SelectMany(t => t.Comments)
                    .Any(c => !c.IsBot && c.PublishedAt > watermark);
            }

            if (!resolveRun)
            {
                var headDecision = DiscoveryFilter.Evaluate(
                    candidate, workItems.Count, prior?.HeadSha, _Rules, hasNewHumanComments);
                if (!headDecision.Interesting)
                {
                    Skip(candidate.Key, headDecision.Reason);
                    return;
                }
            }

            // In-flight check first (P1-12): a live run legitimately owns the PR — the
            // cheap, correct reason ("already in flight") must win the skip attribution
            // over "head failing", and the head-failing-backoff metric must reflect
            // only real failures.
            if (claims.IsClaimed(candidate.Key))
            {
                Skip(candidate.Key, "review already in flight");
                return;
            }

            if (resolveCommand is null)
            {
                var recentRuns = await store.GetRecentRunsAsync(candidate.Key, count: 10, token);
                var blockedUntil = FailureBackoff.BlockedUntil(
                    recentRuns, candidate.Pr.SourceCommitSha, _Clock.GetUtcNow(),
                    new FailureBackoffPolicy(options.FailureBackoffBase, options.FailureBackoffMax));
                if (blockedUntil is not null)
                {
                    Skip(candidate.Key, $"head failing; backoff until {blockedUntil.Value:u}");
                    return;
                }
            }

            // Loop guard suppresses discovery-triggered runs only. An author-issued resolve
            // command is manual and may intentionally address the bot's preceding commit.
            if (resolveCommand is null && autoFix is not null && _Pool is not null)
            {
                var headInfo = await _Pool.GetMirrorCommitInfoAsync(
                    candidate.Key.RepositoryId, candidate.Pr.SourceCommitSha, token);
                if (headInfo is not null && LoopGuard.IsBotAuthoredHead(headInfo, autoFix))
                {
                    ReviewForgeTelemetry.LoopGuardSkips.Add(1, new TagList {{"source", "discovery"}});
                    Skip(candidate.Key, "bot-authored head");
                    return;
                }
            }

            Interlocked.Increment(ref interesting);

            // Cap + claim + enqueue must be atomic relative to other candidates so the
            // per-sweep cap is exact and a failed enqueue always releases its claim.
            Guid? resolveAckRunId = null;
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
                var kind = resolveRun ? RunKind.Resolve : RunKind.Review;
                tracker.Set(runId, candidate.Key, RunState.Queued, kind: kind);
                EnqueueResult result;
                try
                {
                    result = queue.TryEnqueue(new ReviewRequest(
                        runId,
                        candidate.Key,
                        _Clock.GetUtcNow(),
                        Activity.Current?.Context,
                        candidate.Pr.SourceCommitSha,
                        Trigger: resolveCommand is null ? EnqueueTrigger.Discovery : EnqueueTrigger.ResolveCommand,
                        Kind: kind));
                }
                catch (Exception ex)
                {
                    // A durable queue can throw on database/I/O/constraint failures. The
                    // claim and tracker entry were already recorded but no queue item
                    // exists — roll both back so the candidate is not stuck "Queued"
                    // forever and no orphan claim blocks the PR.
                    tracker.Remove(runId);
                    claims.Release(candidate.Key, runId);
                    logger?.LogWarning(ex, "enqueue for {Pr} failed; claim and tracker entry rolled back", candidate.Key);
                    Skip(candidate.Key, "enqueue failed");
                    return;
                }

                if (!result.Accepted)
                {
                    tracker.Remove(runId);
                    claims.Release(candidate.Key, runId);
                    Skip(candidate.Key, "queue full");
                    return;
                }

                ReviewForgeTelemetry.DiscoveryEnqueued.Add(1);
                enqueued.Enqueue(candidate.Key);
                if (resolveCommand is not null) resolveAckRunId = runId;

                // Speculative mirror warmup: prefetch the accepted head's commits so the
                // run's prepare-repository stage skips the origin fetch. Skipped when the
                // checkout already exists (nothing to save) and capped per sweep.
                if (warmups is not null
                    && warmups.Count < options.WarmupMaxPerSweep
                    && !_Pool!.HasCheckout(candidate.Key.RepositoryId, candidate.Pr.SourceCommitSha))
                {
                    warmups.Add(WarmupMirrorAsync(candidate, token));
                }
            }

            if (resolveAckRunId is { } ackRunId && resolveCommand is { } command)
            {
                try
                {
                    await source.ReplyToThreadAsync(candidate.Key, command.ThreadId,
                        CommentFormatter.WithBotPreamble($"👀 Resolve run {ackRunId:D} queued."), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "resolve command acknowledgment failed for {Pr} thread {ThreadId}",
                        candidate.Key, command.ThreadId);
                }
            }
        }

        async Task WarmupMirrorAsync(PullRequestCandidate candidate, CancellationToken token)
        {
            try
            {
                await _WarmupGate!.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await _Pool!.WarmupAsync(
                        candidate.Key.RepositoryId,
                        candidate.Pr.CloneUrl,
                        candidate.Pr.TargetCommitSha,
                        candidate.Pr.SourceCommitSha,
                        token).ConfigureAwait(false);
                }
                finally
                {
                    _WarmupGate.Release();
                }

                _Pool.MarkWarmed(candidate.Key.RepositoryId, candidate.Pr.SourceCommitSha);
                ReviewForgeTelemetry.DiscoveryWarmup.Add(1, new TagList {{ReviewForgeTelemetry.TagResult, "completed"}});
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Warmup is an optimization only: the run's own fetch remains the correctness path.
                ReviewForgeTelemetry.DiscoveryWarmup.Add(1, new TagList {{ReviewForgeTelemetry.TagResult, "failed"}});
                logger?.LogWarning(ex, "mirror warmup for {Pr} failed; the run's own fetch remains the correctness path", candidate.Key);
            }
        }
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