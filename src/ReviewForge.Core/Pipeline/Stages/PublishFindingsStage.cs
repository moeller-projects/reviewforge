using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 9: post findings (inline when the anchor holds, general otherwise), one summary
/// comment with acceptance-criteria verdicts, and set the PAT user's reviewer vote to
/// <see cref="ReviewerVote.WaitingForAuthor"/> when anything needs the author's attention.
/// </summary>
public sealed class PublishFindingsStage(IPullRequestSource source, ILogger<PublishFindingsStage> logger) : IReviewStage
{
    /// <summary>Bounded concurrency for ADO writes; each finding is one HTTP round-trip.</summary>
    public const int MaxConcurrentPosts = 4;

    public string Name => "publish-findings";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (ctx.PublishGuard is not null && !ctx.PublishGuard())
        {
            throw new InvalidOperationException("review claim expired before publication");
        }

        var posted = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        using var gate = new SemaphoreSlim(MaxConcurrentPosts, MaxConcurrentPosts);

        var inlineTasks = ctx.AcceptedFindings
            .Where(f => f is {Anchor: not null, AnchorDowngraded: false})
            .Select(async finding =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var threadId = await source.PostFindingThreadAsync(ctx.Pr, finding, ct).ConfigureAwait(false);
                    posted[finding.DedupeKey!] = threadId;
                }
                finally
                {
                    gate.Release();
                }
            });

        var generalTasks = ctx.AcceptedFindings
            .Where(f => f is not {Anchor: not null, AnchorDowngraded: false})
            .Select(async finding =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await source.PostGeneralCommentAsync(ctx.Pr, CommentFormatter.FormatFinding(finding), ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

        await Task.WhenAll(inlineTasks.Concat(generalTasks)).ConfigureAwait(false);

        ctx.PostedThreadIds = posted;

        // Summary must post AFTER findings (readers of the PR see findings first).
        await source.PostGeneralCommentAsync(ctx.Pr,
                CommentFormatter.FormatSummary(ctx.Result!, ctx.WorkItems, ctx.UnansweredThreads, ctx.Kind), ct)
            .ConfigureAwait(false);

        var acUnmet = (ctx.Result!.Narrative.AcceptanceCriteria ?? []).Any(v => v.Status == AcStatus.Unmet);
        if (ctx.AcceptedFindings.Count > 0 || acUnmet || ctx.UnansweredThreads.Count > 0)
        {
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.CurrentUser!.Id, ReviewerVote.WaitingForAuthor, ct);
            logger.LogInformation("reviewer vote set to waiting-for-author for {User}", ctx.CurrentUser!.DisplayName);
        }
    }
}