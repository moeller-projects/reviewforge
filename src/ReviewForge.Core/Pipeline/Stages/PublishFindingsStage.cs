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
public sealed class PublishFindingsStage(
    IPullRequestSource source,
    IFindingStore store,
    ILogger<PublishFindingsStage> logger) : IReviewStage
{
    /// <summary>Bounded concurrency for ADO writes; each finding is one HTTP round-trip.</summary>
    public const int MaxConcurrentPosts = 4;

    public string Name => "publish-findings";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // Mechanism B: never re-post a finding that already has a live bot thread. The ADO
        // thread properties (ReviewForge.DedupeKey) are the cross-run source of truth and
        // survive a lost/corrupt local store. Fixed/Closed threads do not suppress — a
        // resolved finding that regresses must be re-posted.
        var liveThreadKeys = ctx.Threads
            .Where(t => t.DedupeKey is not null
                        && t.Status is ReviewThreadStatus.Active or ReviewThreadStatus.Pending)
            .Select(t => t.DedupeKey!)
            .ToHashSet(StringComparer.Ordinal);

        var toPost = ctx.AcceptedFindings
            .Where(f => f.DedupeKey is null || !liveThreadKeys.Contains(f.DedupeKey))
            .ToList();

        foreach (var suppressed in ctx.AcceptedFindings.Where(f => f.DedupeKey is not null && liveThreadKeys.Contains(f.DedupeKey)))
        {
            logger.LogInformation("suppressing finding {Key}: live bot thread already exists", suppressed.DedupeKey);
        }

        if (ctx.PublishGuard is not null && !ctx.PublishGuard())
        {
            throw new InvalidOperationException("review claim expired before publication");
        }

        var posted = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        using var gate = new SemaphoreSlim(MaxConcurrentPosts, MaxConcurrentPosts);

        var inlineTasks = toPost
            .Where(f => f is {Anchor: not null, AnchorDowngraded: false})
            .Select(async finding =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var threadId = await source.PostFindingThreadAsync(ctx.Pr, finding, ct).ConfigureAwait(false);
                    posted[finding.DedupeKey!] = threadId;
                    // Mechanism A: durable per-finding record immediately after the post, so a
                    // crash before finalize never loses the fact that this finding was posted.
                    await store.SetThreadIdAsync(ctx.RunId, finding.DedupeKey!, threadId, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

        var generalTasks = toPost
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