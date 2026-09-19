using ReviewForge.Core.Pipeline;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>
/// Single worker draining the ingest queue. A failed run is logged and marked Failed —
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
                using var ctx = new ReviewContext(request.Pr, _Clock.GetUtcNow(), request.RunId);
                await pipelineFactory.Create().RunAsync(ctx, stoppingToken);

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