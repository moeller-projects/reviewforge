using System.Diagnostics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Logging;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>
/// Worker draining the bounded ingest queue. A failed run is logged, marked Failed, and
/// persisted as a failure record (so discovery backoff has memory) — the worker keeps
/// draining (no poison-message shutdown).
/// </summary>
public sealed class ReviewWorker(
    ReviewQueue queue,
    RunTracker tracker,
    ReviewPipelineFactory pipelineFactory,
    InFlightClaims claims,
    IFindingStore store,
    ILogger<ReviewWorker> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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

            // The claim was taken at enqueue with a finite TTL; if this request sat
            // queued past expiry, another run may own the PR now. Revalidate before
            // spending an LLM run: re-claim if free, skip if a different run holds it.
            if (!claims.IsHeldBy(request.Pr, request.RunId)
                && !claims.TryClaim(request.Pr, request.RunId, out var holder))
            {
                logger.LogInformation(
                    "skipping run {RunId} for {Pr}: claim lost while queued (held by {Holder})",
                    request.RunId, request.Pr, holder);
                tracker.Set(request.RunId, request.Pr, RunState.Skipped, "claim lost while queued");
                RunLogFileProvider.Current?.CloseRun(request.RunId);
                continue; // finally-block of the run loop is not entered; nothing to release
            }

            tracker.Set(request.RunId, request.Pr, RunState.Running);
            logger.LogInformation("review run {RunId} started for {Pr}", request.RunId, request.Pr);
            var repoTag = new TagList { { ReviewForgeTelemetry.TagRepoId, request.Pr.RepositoryId } };
            ReviewForgeTelemetry.ReviewsStarted.Add(1, repoTag);
            var runStart = Stopwatch.GetTimestamp();
            ReviewContext? ctx = null;
            try
            {
                ctx = new ReviewContext(request.Pr, _Clock.GetUtcNow(), request.RunId)
                {
                    PublishGuard = () => claims.IsHeldBy(request.Pr, request.RunId),
                    EnqueueContext = request.EnqueueContext,
                };

                // Keep the reservation alive for the whole run so a review that outlives the
                // claim TTL does not admit a duplicate; the publish guard still fails the run
                // safely if the claim is ever lost.
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = new ClaimHeartbeat(
                        claims, request.Pr, request.RunId,
                        TimeSpan.FromTicks(Math.Max(claims.Ttl.Ticks / 4, TimeSpan.FromSeconds(1).Ticks)))
                    .RunUntilCancelled(heartbeatCts.Token);
                try
                {
                    await pipelineFactory.Create().RunAsync(ctx, stoppingToken);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    await heartbeat.ConfigureAwait(false);
                }

                var state = ctx.Terminated ? RunState.Skipped : RunState.Completed;
                tracker.Set(request.RunId, request.Pr, state, ctx.TerminationReason);
                logger.LogInformation("review run {RunId} {State}: {Reason}",
                    request.RunId, state, ctx.TerminationReason ?? "ok");
                var tags = repoTag;
                tags.Add(ReviewForgeTelemetry.TagResult, ctx.Terminated ? "skipped" : "completed");
                ReviewForgeTelemetry.ReviewsCompleted.Add(1, tags);
                ReviewForgeTelemetry.ReviewDurationMilliseconds.Record(
                    Stopwatch.GetElapsedTime(runStart).TotalMilliseconds, tags);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (PrHeadChangedException ex)
            {
                // A force-push mid-run is a normal race, not an operational failure: no
                // error page, no wrong-iteration writes. The sweep reviews the new head.
                logger.LogInformation(
                    "run {RunId} for {Pr} superseded mid-run (head moved from {Old} to {New})",
                    request.RunId, request.Pr, ex.Expected, ex.Actual);
                tracker.Set(request.RunId, request.Pr, RunState.Failed, ex.Message);
                await PersistFailureAsync(request, ctx, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "run {RunId} for {Pr} failed", request.RunId, request.Pr);
                tracker.Set(request.RunId, request.Pr, RunState.Failed, ex.Message);
                var tags = repoTag;
                tags.Add(ReviewForgeTelemetry.TagResult, "failed");
                ReviewForgeTelemetry.ReviewsCompleted.Add(1, tags);
                ReviewForgeTelemetry.ReviewDurationMilliseconds.Record(
                    Stopwatch.GetElapsedTime(runStart).TotalMilliseconds, tags);
                await PersistFailureAsync(request, ctx, stoppingToken);
            }
            finally
            {
                ctx?.Dispose();
                claims.Release(request.Pr, request.RunId);
                RunLogFileProvider.Current?.CloseRun(request.RunId);
            }
        }
    }

    /// <summary>Best-effort failure record so discovery backoff has memory. Never throws.</summary>
    private async Task PersistFailureAsync(ReviewRequest request, ReviewContext? ctx, CancellationToken ct)
    {
        try
        {
            await store.SaveRunAsync(new ReviewRun(
                request.RunId,
                request.Pr,
                ctx?.PullRequest?.SourceCommitSha ?? request.HeadSha ?? string.Empty,
                ctx?.Kind ?? ReviewKind.Full,
                ctx?.StartedAt ?? request.EnqueuedAt,
                _Clock.GetUtcNow(),
                Success: false,
                Findings: []), ct);
        }
        catch (Exception storeEx)
        {
            logger.LogWarning(storeEx, "failed to persist failure record for run {RunId}", request.RunId);
        }
    }
}
