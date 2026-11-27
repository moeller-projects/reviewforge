using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Telemetry;

namespace ReviewForge.Service;

/// <summary>
/// Configured consumers drain the bounded ingest queue concurrently. A failed run is logged,
/// marked Failed, and persisted; a failed review does not stop the remaining consumers.
/// </summary>
public sealed class ReviewWorker(
    IReviewQueue queue,
    IOptions<HostOptions> hostOptions,
    RunTracker tracker,
    IPipelineBuilder pipelineBuilder,
    InFlightClaims claims,
    IFindingStore store,
    ILogger<ReviewWorker> logger,
    TimeProvider? clock = null,
    IOptions<ModelProfilingOptions>? profilingOptions = null) : BackgroundService
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var group = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        ExceptionDispatchInfo? failure = null;
        async Task ConsumeAsync()
        {
            try { await ProcessQueueAsync(group.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (group.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                await group.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
        var workers = new Task[hostOptions.Value.WorkerCount];
        // Schedule independently; a fault cancels siblings so WhenAll can surface it
        // after their bounded cleanup instead of silently losing consumer capacity.
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(ConsumeAsync, group.Token);
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch { failure?.Throw(); throw; }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            using var runScope = logger.BeginScope(new Dictionary<string, object>
            {
                ["RunId"] = request.RunId,
                ["Repo"] = request.Pr.RepositoryId,
                ["PrId"] = request.Pr.PrId,
                ["Pr"] = request.Pr.ToString(),
            });

            // Durable requests may outlive this process and have no tracker entry yet.
            tracker.Register(request);

            // The claim was taken at enqueue with a finite TTL; if this request sat
            // queued past expiry, another run may own the PR now. Revalidate before
            // spending an LLM run: re-claim if free, skip if a different run holds it.
            if (!claims.IsHeldBy(request.Pr, request.RunId)
                && !claims.TryClaim(request.Pr, request.RunId, out var holder))
            {
                logger.LogInformation(
                    "skipping run {RunId} for {Pr}: claim lost while queued (held by {Holder})",
                    request.RunId, request.Pr, holder);
                tracker.Set(request.RunId, request.Pr, RunState.Skipped, "claim lost while queued", request.Kind);
                // The request was already dequeued (claimed) from the queue; without this
                // ack a durable row would stay claimed and be reclaimed forever.
                queue.Acknowledge(request.RunId);
                continue; // finally-block of the run loop is not entered; nothing to release
            }

            var repoTag = new TagList {{ReviewForgeTelemetry.TagRepoId, request.Pr.RepositoryId}, {"kind", request.Kind.ToString().ToLowerInvariant()}};
            var runStart = Stopwatch.GetTimestamp();
            ReviewContext? ctx = null;
            try
            {
                // A crash after terminal profile commit but before queue ack must not run the
                // same execution again. Finalized is independent of detail expiry, emptiness,
                // truncation and capture completeness. Marker-only interruptions still resume.
                if (store is IModelProfileStore profiles &&
                    (await profiles.ReadModelProfileAsync(request.RunId, 0, 1, _Clock.GetUtcNow(), stoppingToken)).Finalized)
                {
                    var terminal = await store.GetRunAsync(request.RunId, stoppingToken);
                    if (terminal?.CompletedAt is null)
                        throw new InvalidOperationException("Finalized profile has no terminal run.");
                    tracker.Set(request.RunId, request.Pr, terminal.Success ? RunState.Completed : RunState.Failed,
                        kind: request.Kind);
                    logger.LogInformation("run {RunId} already finalized; acknowledging reclaimed request", request.RunId);
                    queue.Acknowledge(request.RunId);
                    continue; // finally releases only this run's PR claim
                }

                ctx = new ReviewContext(request.Pr, _Clock.GetUtcNow(), request.RunId,
                    profilingOptions?.Value.DetailLimit ?? 10_000, _Clock)
                {
                    RunKind = request.Kind,
                    PublishGuard = () => claims.IsHeldBy(request.Pr, request.RunId),
                    EnqueueContext = request.EnqueueContext,
                    Trigger = request.Trigger,
                    ForceReview = request.ForceReview,
                };

                tracker.Set(request.RunId, request.Pr, RunState.Running, kind: request.Kind);
                logger.LogInformation("review run {RunId} started for {Pr}", request.RunId, request.Pr);
                ReviewTelemetry.ReviewsStarted.Add(1, repoTag);

                // Keep the reservation alive for the whole run so a review that outlives the
                // claim TTL does not admit a duplicate; the publish guard still fails the run
                // safely if the claim is ever lost. The durable queue's claim lease (default
                // 10 min) renews on the same tick, so the interval is capped well under it —
                // the in-memory TTL alone would allow a 30-minute cadence.
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeatInterval = TimeSpan.FromTicks(Math.Max(claims.Ttl.Ticks / 4, TimeSpan.FromSeconds(1).Ticks));
                if (heartbeatInterval > TimeSpan.FromMinutes(3))
                {
                    heartbeatInterval = TimeSpan.FromMinutes(3);
                }

                var heartbeat = new ClaimHeartbeat(
                        claims, request.Pr, request.RunId, heartbeatInterval,
                        time: _Clock,
                        renewDurableClaim: () => queue.RenewClaim(request.RunId))
                    .RunUntilCancelled(heartbeatCts.Token);
                try
                {
                    await pipelineBuilder.Build(request.Kind, request, ctx,
                        new TrackedRunProgress(tracker, request.RunId)).RunAsync(ctx, stoppingToken);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    await heartbeat.ConfigureAwait(false);
                }

                if (!ctx.Terminated)
                    await FinalizeProfileAsync(ctx, false, TimeSpan.FromSeconds(5));
                var state = ctx.Terminated == true ? RunState.Skipped : RunState.Completed;
                tracker.Set(request.RunId, request.Pr, state, ctx.TerminationReason, request.Kind);
                logger.LogInformation("run {RunId} {State}: {Reason}",
                    request.RunId, state, ctx.TerminationReason ?? "ok");
                var tags = repoTag;
                tags.Add(ReviewForgeTelemetry.TagResult, ctx.Terminated == true ? "skipped" : "completed");
                ReviewTelemetry.ReviewsCompleted.Add(1, tags);
                ReviewTelemetry.ReviewDurationMilliseconds.Record(
                    Stopwatch.GetElapsedTime(runStart).TotalMilliseconds, tags);
            }
            catch (Exception ex) when (ctx is null)
            {
                // Preflight is not execution: never replace an existing terminal result or
                // acknowledge a request whose durable status could not be established.
                logger.LogError(ex, "preflight failed for run {RunId}; durable records preserved and request left for reclamation", request.RunId);
                throw;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown mid-run: leave a truthful failure record. Best-effort with a
                // hard timeout — never the (cancelled) stoppingToken — and if the store is
                // already torn down the startup ShellReaperService finalizes the orphaned
                // shell on next boot. The tracker must reach the same terminal state as the
                // persisted row: a leftover Running entry would read as alive until the
                // tracker's own eviction.
                tracker.Set(request.RunId, request.Pr, RunState.Failed, "cancelled by host shutdown", request.Kind);
                await PersistFailureAsync(request, ctx, TimeSpan.FromSeconds(5));
                return;
            }
            catch (PrHeadChangedException ex)
            {
                // A force-push mid-run is a normal race, not an operational failure: no
                // error page, no wrong-iteration writes. The sweep reviews the new head.
                logger.LogInformation(
                    "run {RunId} for {Pr} superseded mid-run (head moved from {Old} to {New})",
                    request.RunId, request.Pr, ex.Expected, ex.Actual);
                tracker.Set(request.RunId, request.Pr, RunState.Failed, ex.Message, request.Kind);
                await PersistFailureAsync(request, ctx, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "run {RunId} for {Pr} failed", request.RunId, request.Pr);
                // Raw exception messages can carry internal paths/URLs — keep detail in
                // the logs, expose a generic reason through the run-status API.
                tracker.Set(request.RunId, request.Pr, RunState.Failed, "internal error — see run log", request.Kind);
                var tags = repoTag;
                tags.Add(ReviewForgeTelemetry.TagResult, "failed");
                ReviewTelemetry.ReviewsCompleted.Add(1, tags);
                ReviewTelemetry.ReviewDurationMilliseconds.Record(
                    Stopwatch.GetElapsedTime(runStart).TotalMilliseconds, tags);
                await PersistFailureAsync(request, ctx, TimeSpan.FromSeconds(5));
            }
            finally
            {
                // Only executions we actually started are acked here. Successful preflight
                // of a finalized request acknowledges explicitly; failed preflight remains reclaimable.
                if (ctx is not null) queue.Acknowledge(request.RunId);
                ctx?.Dispose();
                claims.Release(request.Pr, request.RunId);
            }
        }
    }

    private async Task FinalizeProfileAsync(ReviewContext ctx, bool incomplete, TimeSpan timeout)
    {
        if (store is not IModelProfileStore profiles)
            throw new InvalidOperationException("The finding store must support model profiling.");
        using var token = new CancellationTokenSource(timeout);
        var completed = _Clock.GetUtcNow();
        ctx.ModelProfile.Resume(await profiles.BeginModelProfileAsync(ctx.ModelProfile.Snapshot(), token.Token));
        await profiles.FinalizeModelProfileAsync(ctx.ModelProfile.Snapshot(), completed,
            completed.AddDays(profilingOptions?.Value.RetentionDays ?? 30), incomplete, token.Token);
    }

    /// <summary>
    /// Best-effort failure record so discovery backoff has memory. Never throws.
    /// Uses a hard timeout — never the possibly-cancelled run token.
    /// </summary>
    private async Task PersistFailureAsync(ReviewRequest request, ReviewContext? ctx, TimeSpan timeout)
    {
        // A journal/notification failure after a gate skip must not turn the skip into a stored run.
        if (ctx?.Terminated == true) return;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            await store.SaveRunAsync(new ReviewRun(
                request.RunId,
                request.Pr,
                ctx?.Fetch.PullRequest?.SourceCommitSha ?? request.HeadSha ?? string.Empty,
                ctx?.Classification.Kind ?? ReviewKind.Full,
                ctx?.StartedAt ?? request.EnqueuedAt,
                _Clock.GetUtcNow(),
                Success: false,
                Findings: [],
                Pipeline: request.Kind.ToString(),
                FindingObservations: ctx?.SnapshotFindingObservations(),
                Diagnostics: ctx?.SnapshotDiagnostics()), timeoutCts.Token);
            if (ctx is not null) await FinalizeProfileAsync(ctx, false, timeout);
        }
        catch (Exception storeEx)
        {
            logger.LogWarning(storeEx, "failed to persist failure record for run {RunId}", request.RunId);
        }
    }
}