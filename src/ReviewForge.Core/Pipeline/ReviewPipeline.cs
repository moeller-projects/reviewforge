using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ReviewForge.Core.Pipeline;

/// <summary>
/// Sequential stage runner. One Activity per stage; a failed stage faults the run —
/// the host worker catches, logs, and marks it Failed (<c>ReviewWorker</c>), no engine
/// fallback. Graceful early exit via <see cref="ReviewContext.Terminate"/>.
/// </summary>
public sealed class ReviewPipeline
{
    private readonly IReadOnlyList<IReviewStage> _Stages;
    private readonly ILogger<ReviewPipeline> _Logger;

    public ReviewPipeline(IEnumerable<IReviewStage> stages, ILogger<ReviewPipeline> logger)
    {
        _Stages = [.. stages];
        // Stage names feed telemetry and log scopes; an accidental duplicate is the real
        // failure mode now that the injected list is the only source of sequencing truth.
        var dupes = _Stages.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (dupes.Length > 0)
        {
            throw new InvalidOperationException($"duplicate stages: {string.Join(", ", dupes)}");
        }

        _Logger = logger;
    }

    public async Task<ReviewContext> RunAsync(ReviewContext ctx, CancellationToken ct)
    {
        var links = ctx.EnqueueContext is { } enqueueCtx
            ? new[] {new ActivityLink(enqueueCtx)}
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

                using var stageScope = _Logger.BeginScope(new Dictionary<string, object>
                {
                    ["Stage"] = stage.Name,
                });

                using var stageActivity = ReviewForgeTelemetry.Source.StartActivity($"stage.{stage.Name}");
                stageActivity?.SetTag(ReviewForgeTelemetry.TagRunId, ctx.RunId.ToString());
                stageActivity?.SetTag(ReviewForgeTelemetry.TagRepoId, ctx.Pr.RepositoryId);
                stageActivity?.SetTag(ReviewForgeTelemetry.TagPrId, ctx.Pr.PrId);
                stageActivity?.SetTag(ReviewForgeTelemetry.TagStage, stage.Name);

                _Logger.LogInformation("stage {Stage} starting", stage.Name);
                var sw = Stopwatch.StartNew();
                var stageResult = "completed";
                try
                {
                    await stage.ExecuteAsync(ctx, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    stageResult = "cancelled";
                    throw;
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
                    ReviewTelemetry.StageDurationMilliseconds.Record(
                        sw.ElapsedMilliseconds,
                        new TagList
                        {
                            {ReviewForgeTelemetry.TagStage, stage.Name},
                            {ReviewForgeTelemetry.TagResult, stageResult},
                        });
                    _Logger.LogInformation("stage {Stage} done in {ElapsedMs} ms", stage.Name, sw.ElapsedMilliseconds);
                }

                if (headScope is null && ctx.Fetch.PullRequest is not null)
                {
                    headScope = _Logger.BeginScope(new Dictionary<string, object>
                    {
                        ["HeadSha"] = ctx.Fetch.PullRequest.SourceCommitSha,
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
            _Logger.LogInformation("run terminated early: {Reason}", ctx.TerminationReason);
        }

        if (ctx.Fetch.PullRequest is not null)
        {
            runActivity?.SetTag(ReviewForgeTelemetry.TagHeadSha, ctx.Fetch.PullRequest.SourceCommitSha);
        }

        return ctx;
    }
}