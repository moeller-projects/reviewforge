using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Host-owned notifications; Core stages retain their telemetry names and know nothing of status tracking.</summary>
public interface IRunProgress
{
    void DefinitionLoaded(IReadOnlyList<StageId> stages);
    void StageStarting(StageId stage);
    void ContextFetched(string headSha);
}

public sealed class TrackedRunProgress(RunTracker tracker, Guid runId) : IRunProgress
{
    public void DefinitionLoaded(IReadOnlyList<StageId> stages) => tracker.DefinitionLoaded(runId, stages);
    public void StageStarting(StageId stage) => tracker.StageStarting(runId, stage);
    public void ContextFetched(string headSha) => tracker.CaptureHead(runId, headSha);
}

public sealed class ProgressStage(StageId id, IReviewStage inner, IRunProgress? progress = null,
    IFindingStore? store = null, TimeProvider? clock = null) : IReviewStage
{
    public string Name => inner.Name;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var time = clock ?? TimeProvider.System;
        ctx.DiagnosticTraceId ??= System.Diagnostics.Activity.Current?.TraceId.ToString();
        progress?.StageStarting(id);
        var index = ctx.StageExecutions.Count;
        var timestamp = time.GetTimestamp();
        var started = new StageExecutionDiagnostic(id.ToString(), time.GetUtcNow(), null, null, "running", null);
        ctx.StageExecutions.Add(started);
        try
        {
            if (store is not null && ctx.DiagnosticsShellExists)
                await store.UpdateRunDiagnosticsAsync(ctx.RunId, ctx.SnapshotDiagnostics(), ct).ConfigureAwait(false);
            await inner.ExecuteAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only stable classifier codes are persisted; the original exception is rethrown unchanged.
            ctx.StageExecutions[index] = started with {
                CompletedAt = time.GetUtcNow(), DurationMilliseconds = time.GetElapsedTime(timestamp).TotalMilliseconds,
                Outcome = ex is OperationCanceledException ? "cancelled" : "failed",
                FailureCode = ex switch {
                    OperationCanceledException => "cancelled",
                    PrHeadChangedException => "head-changed",
                    _ => "stage-failed",
                } };
            // Worker failure finalization writes this snapshot with a fresh, bounded token.
            throw;
        }
        ctx.StageExecutions[index] = started with {
            CompletedAt = time.GetUtcNow(), DurationMilliseconds = time.GetElapsedTime(timestamp).TotalMilliseconds,
            Outcome = ctx.Terminated ? "skipped" : "completed" };
        if (id is StageId.BeginRun or StageId.BeginDraftRun or StageId.PersistRun or StageId.PersistDraftRun)
            ctx.DiagnosticsShellExists = true;
        try
        {
            if (store is not null && ctx.DiagnosticsShellExists && !ctx.Terminated)
                await store.UpdateRunDiagnosticsAsync(ctx.RunId, ctx.SnapshotDiagnostics(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.StageExecutions[index] = ctx.StageExecutions[index] with
            {
                CompletedAt = time.GetUtcNow(), DurationMilliseconds = time.GetElapsedTime(timestamp).TotalMilliseconds,
                Outcome = ex is OperationCanceledException ? "cancelled" : "failed",
                FailureCode = ex is OperationCanceledException ? "journal-write-cancelled" : "journal-write-failed",
            };
            throw;
        }
        if (id == StageId.FetchPrContext && ctx.Fetch.PullRequest is { } pr)
            progress?.ContextFetched(pr.SourceCommitSha);
    }
}
