using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 4: re-fetch threads (new comments may have arrived during preparation),
/// classify full vs follow-up, extract pending human replies on bot threads.
/// </summary>
public sealed class ClassifyRunStage(IPullRequestSource source) : IReviewStage
{
    public string Name => "classify-run";

    public int Order => 40;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Threads = await source.GetThreadsAsync(ctx.Pr, ct);
        ctx.Kind = RunClassifier.Classify(ctx.PriorRun);
        ctx.PendingReplies = RunClassifier.PendingReplies(ctx.Threads);
    }
}