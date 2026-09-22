using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 1: PR metadata, linked work items, changed files, threads, PAT identity, prior run.</summary>
public sealed class FetchPrContextStage(IPullRequestSource source, IFindingStore store) : IReviewStage
{
    public string Name => "fetch-pr-context";

    public int Order => 10;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var prTask = source.GetPullRequestAsync(ctx.Pr, ct);
        var workItemsTask = source.GetLinkedWorkItemsAsync(ctx.Pr, ct);
        var filesTask = source.GetChangedFilesAsync(ctx.Pr, ct);
        var threadsTask = source.GetThreadsAsync(ctx.Pr, ct);
        var userTask = source.GetCurrentUserAsync(ct);
        var priorRunTask = store.GetLastCompletedRunAsync(ctx.Pr, ct);

        await Task.WhenAll(prTask, workItemsTask, filesTask, threadsTask, userTask, priorRunTask);

        ctx.PullRequest = await prTask;
        ctx.WorkItems = await workItemsTask;
        ctx.ChangedFileManifest = await filesTask;
        ctx.Threads = await threadsTask;
        ctx.CurrentUser = await userTask;
        ctx.PriorRun = await priorRunTask;
    }
}