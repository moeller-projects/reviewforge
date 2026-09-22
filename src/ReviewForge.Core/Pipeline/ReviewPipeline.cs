using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ReviewForge.Core.Pipeline;

/// <summary>
/// Sequential stage runner. One Activity per stage; a failed stage faults the run —
/// the host worker catches, logs, and marks it Failed (<c>ReviewWorker</c>), no engine
/// fallback. Graceful early exit via <see cref="ReviewContext.Terminate"/>.
/// </summary>
public sealed class ReviewPipeline(IEnumerable<IReviewStage> stages, ILogger<ReviewPipeline> logger)
{
    private readonly IReadOnlyList<IReviewStage> _Stages = [.. stages];

    public async Task<ReviewContext> RunAsync(ReviewContext ctx, CancellationToken ct)
    {
        var links = ctx.EnqueueContext is { } enqueueCtx
            ? new[] { new ActivityLink(enqueueCtx) }
            : null;

        using var runActivity = ReviewForgeTelemetry.Source.StartActivity(
            "review.run", ActivityKind.Internal, default(ActivityContext), links: links);
        runActivity?.SetTag(ReviewForgeTelemetry.TagRunId, ctx.RunId.ToString());
        runActivity?.SetTag(ReviewForgeTelemetry.TagPrId, ctx.Pr.PrId);
        runActivity?.SetTag(ReviewForgeTelemetry.TagRepoId, ctx.Pr.RepositoryId);
        runActivity?.SetTag(ReviewForgeTelemetry.TagOrg, ctx.Pr.Org);
        runActivity?.SetTag(ReviewForgeTelemetry.TagProject, ctx.Pr.Project);

        IDisposable? headScope = null;
        try
        {
            foreach (var stage in _Stages)
            {
                if (ctx.Terminated)
                {
                    break;
                }

                using var stageScope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["Stage"] = stage.Name,
                });

                using var stageActivity = ReviewForgeTelemetry.Source.StartActivity($"stage.{stage.Name}");
                stageActivity?.SetTag(ReviewForgeTelemetry.TagRunId, ctx.RunId.ToString());
                stageActivity?.SetTag(ReviewForgeTelemetry.TagRepoId, ctx.Pr.RepositoryId);
                stageActivity?.SetTag(ReviewForgeTelemetry.TagPrId, ctx.Pr.PrId);
                stageActivity?.SetTag(ReviewForgeTelemetry.TagStage, stage.Name);

                logger.LogInformation("stage {Stage} starting", stage.Name);
                var sw = Stopwatch.StartNew();
                var stageResult = "completed";
                try
                {
                    await stage.ExecuteAsync(ctx, ct);
                }
                catch (Exception ex)
                {
                    stageResult = "failed";
                    stageActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    stageActivity?.AddException(ex);
                    runActivity?.SetStatus(ActivityStatusCode.Error, $"stage {stage.Name}: {ex.Message}");
                    throw;
                }
                finally
                {
                    sw.Stop();
                    ReviewForgeTelemetry.StageDurationMilliseconds.Record(
                        sw.ElapsedMilliseconds,
                        new TagList
                        {
                            { ReviewForgeTelemetry.TagStage, stage.Name },
                            { ReviewForgeTelemetry.TagResult, stageResult },
                            { ReviewForgeTelemetry.TagRepoId, ctx.Pr.RepositoryId },
                        });
                    logger.LogInformation("stage {Stage} done in {ElapsedMs} ms", stage.Name, sw.ElapsedMilliseconds);
                }

                if (headScope is null && ctx.PullRequest is not null)
                {
                    headScope = logger.BeginScope(new Dictionary<string, object>
                    {
                        ["HeadSha"] = ctx.PullRequest.SourceCommitSha,
                    });
                }
            }
        }
        finally
        {
            headScope?.Dispose();
        }

        if (ctx.Terminated)
        {
            runActivity?.SetTag("reviewforge.terminated", ctx.TerminationReason);
            logger.LogInformation("run terminated early: {Reason}", ctx.TerminationReason);
        }

        if (ctx.PullRequest is not null)
        {
            runActivity?.SetTag(ReviewForgeTelemetry.TagHeadSha, ctx.PullRequest.SourceCommitSha);
        }

        return ctx;
    }
}