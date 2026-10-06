using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class CollectCommentsStage(
    IPullRequestSource source,
    IFindingStore store,
    IReadOnlySet<string> allowedCommenters,
    TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    public string Name => "collect-comments";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        ctx.Threads = await ResolveThreadsAsync(ctx, ct).ConfigureAwait(false);
        var priorByThread = (await store.GetResolveActionsAsync(ctx.Pr, [], ct).ConfigureAwait(false))
            .ToDictionary(action => action.ThreadId);
        var authorOnly = allowedCommenters.Count == 0;
        var comments = new List<ResolvableComment>();
        foreach (var thread in ctx.Threads)
        {
            priorByThread.TryGetValue(thread.Id, out var previousAction);
            var deferred = previousAction?.Outcome == ResolutionOutcome.Deferred;
            if (thread.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed || thread.Comments.Count == 0)
                continue;
            if (!deferred && (thread.Comments[^1].IsBot || (thread.DedupeKey is not null && !thread.HasPendingHumanReply)))
                continue;
            var latestHuman = thread.Comments.LastOrDefault(c => !c.IsBot);
            if (latestHuman is null
                || (!deferred && ctx.ResolveWatermark is { } watermark && latestHuman.PublishedAt <= watermark)
                || (!deferred && previousAction is not null && latestHuman.PublishedAt <= previousAction.CreatedAt))
                continue;
            // "/fixit" belongs to the auto-fix pipeline. "/resolve" stays: the command
            // comment IS a resolution request (its text may carry the actual instructions);
            // dropping it would leave a command-only PR with nothing to resolve even though
            // the command was acknowledged.
            if (Command(latestHuman.Text, "/fixit"))
                continue;
            var allowed = authorOnly
                ? string.Equals(latestHuman.AuthorId, ctx.RequirePullRequest().CreatorId, StringComparison.OrdinalIgnoreCase)
                : allowedCommenters.Contains(latestHuman.AuthorId);
            comments.Add(new ResolvableComment(
                thread.Id,
                thread.Anchor is null ? null : thread.Anchor with {FilePath = RepoPath.Normalize(thread.Anchor.FilePath)},
                latestHuman.AuthorId,
                latestHuman.AuthorName,
                thread.Comments,
                latestHuman.Text.Length <= 1000 ? latestHuman.Text : latestHuman.Text[..1000],
                allowed));
        }

        ctx.ResolvableComments = comments;
    }

    private async Task<IReadOnlyList<ReviewThread>> ResolveThreadsAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (ctx.PendingThreadsRefresh is { } refresh)
        {
            var wasInFlight = !refresh.Task.IsCompleted;
            // No silent fallback: a failed overlap refresh fails the run. Refetching after a
            // discarded provider failure would hide the original error and let a failed
            // stage report success.
            var overlapped = await refresh.Task.ConfigureAwait(false);
            var completedAt = refresh.CompletedAt ?? (wasInFlight ? _Clock.GetUtcNow() : (DateTimeOffset?) null);
            if (completedAt is { } receivedAt
                && ctx.RepoPreparedAt is { } preparedAt && receivedAt >= preparedAt)
                return overlapped;
        }

        return await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
    }

    private static bool Command(string text, string command)
        => text.TrimStart().StartsWith(command, StringComparison.OrdinalIgnoreCase)
           && (text.TrimStart().Length == command.Length || char.IsWhiteSpace(text.TrimStart()[command.Length]));
}