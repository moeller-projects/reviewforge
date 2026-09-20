using ReviewForge.Core.Pipeline;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>
/// Worker draining the bounded ingest queue. A failed run is logged and marked Failed —
/// the worker keeps draining (no poison-message shutdown).
/// </summary>
public sealed class ReviewWorker(
    ReviewQueue queue,
    RunTracker tracker,
    ReviewPipelineFactory pipelineFactory,
    InFlightClaims claims,
    ILogger<ReviewWorker> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            tracker.Set(request.RunId, request.Pr, RunState.Running);
            try
            {
                using var ctx = new ReviewContext(request.Pr, _Clock.GetUtcNow(), request.RunId)
                {
                    PublishGuard = () => claims.IsHeldBy(request.Pr, request.RunId),
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

                tracker.Set(request.RunId, request.Pr,
                    ctx.Terminated ? RunState.Skipped : RunState.Completed,
                    ctx.TerminationReason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "run {RunId} for {Pr} failed", request.RunId, request.Pr);
                tracker.Set(request.RunId, request.Pr, RunState.Failed, ex.Message);
            }
            finally
            {
                claims.Release(request.Pr, request.RunId);
            }
        }
    }
}