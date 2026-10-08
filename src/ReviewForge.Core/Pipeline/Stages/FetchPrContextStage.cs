using Microsoft.Extensions.Logging;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>Stage 1: PR metadata, linked work items, changed files, threads, PAT identity, prior run.</summary>
public sealed class FetchPrContextStage(IPullRequestSource source, IFindingStore store, ILogger<FetchPrContextStage>? logger = null) : IReviewStage
{
    public string Name => "fetch-pr-context";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        logger?.LogDebug("fetching PR context for {Pr}: parallel metadata requests starting", ctx.Pr);
        var prTask = source.GetPullRequestAsync(ctx.Pr, ct);
        var workItemsTask = source.GetLinkedWorkItemsAsync(ctx.Pr, ct);
        var filesTask = source.GetChangedFilesAsync(ctx.Pr, ct);
        var threadsTask = source.GetThreadsAsync(ctx.Pr, ct);
        var userTask = source.GetCurrentUserAsync(ct);
        var priorRunTask = store.GetLastCompletedRunAsync(ctx.Pr, ct);

        await Task.WhenAll(prTask, workItemsTask, filesTask, threadsTask, userTask, priorRunTask);

        ctx.Fetch = new FetchOutcome
        {
            PullRequest = await prTask,
            WorkItems = await workItemsTask,
            ChangedFileManifest = await filesTask,
            Threads = await threadsTask,
            CurrentUser = await userTask,
            PriorRun = await priorRunTask
        };
        logger?.LogDebug("fetched PR context for {Pr}: files={FileCount}, threads={ThreadCount}, workItems={WorkItemCount}, priorRun={HasPriorRun}, currentUser={HasCurrentUser}",
            ctx.Pr, ctx.Fetch.ChangedFileManifest.Count, ctx.Fetch.Threads.Count, ctx.Fetch.WorkItems.Count,
            ctx.Fetch.PriorRun is not null, ctx.Fetch.CurrentUser is not null);
        // Prior findings are bot-authored, not PR-supplied; read_context's <pr-supplied-data>
        // wrapping is belt-and-suspenders and deliberately reused here.
        if (PriorReviewContextBuilder.Build(ctx.Fetch.PriorRun, ctx.Fetch.Threads) is { } memory)
        {
            ctx.Reasoning.ContextStore.Put(PriorReviewContextBuilder.ContextName, memory);
            logger?.LogDebug("stored prior review context for {Pr}", ctx.Pr);
        }
    }
}