using ReviewForge.Core.Pipeline;

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

public sealed class ProgressStage(StageId id, IReviewStage inner, IRunProgress progress) : IReviewStage
{
    public string Name => inner.Name;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        progress.StageStarting(id);
        await inner.ExecuteAsync(ctx, ct);
        if (id == StageId.FetchPrContext && ctx.Fetch.PullRequest is { } pr)
            progress.ContextFetched(pr.SourceCommitSha);
    }
}
