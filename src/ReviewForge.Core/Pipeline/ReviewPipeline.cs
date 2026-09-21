using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ReviewForge.Core.Pipeline;

/// <summary>
/// Sequential stage runner. One Activity per stage; a failed stage faults the run
/// (exit code 1 at the host boundary — no engine fallback). Graceful early exit via
/// <see cref="ReviewContext.Terminate"/>.
/// </summary>
public sealed class ReviewPipeline(IEnumerable<IReviewStage> stages, ILogger<ReviewPipeline> logger)
{
    private readonly IReadOnlyList<IReviewStage> _Stages = [.. stages];

    public async Task<ReviewContext> RunAsync(ReviewContext ctx, CancellationToken ct)
    {
        using var runActivity = ReviewForgeTelemetry.Source.StartActivity("review.run");
        runActivity?.SetTag("reviewforge.pr", ctx.Pr.ToString());
        runActivity?.SetTag("reviewforge.run_id", ctx.RunId.ToString());

        foreach (var stage in _Stages)
        {
            if (ctx.Terminated)
            {
                break;
            }

            using var stageActivity = ReviewForgeTelemetry.Source.StartActivity($"stage.{stage.Name}");
            logger.LogInformation("stage {Stage} starting", stage.Name);
            var sw = Stopwatch.StartNew();

            await stage.ExecuteAsync(ctx, ct);

            sw.Stop();
            stageActivity?.SetTag("duration_ms", sw.ElapsedMilliseconds);
            logger.LogInformation("stage {Stage} done in {ElapsedMs} ms", stage.Name, sw.ElapsedMilliseconds);
        }

        if (ctx.Terminated)
        {
            runActivity?.SetTag("reviewforge.terminated", ctx.TerminationReason);
            logger.LogInformation("run terminated early: {Reason}", ctx.TerminationReason);
        }

        return ctx;
    }
}