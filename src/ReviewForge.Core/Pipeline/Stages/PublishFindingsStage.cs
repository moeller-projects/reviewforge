using Microsoft.Extensions.Logging;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 9: post findings (inline when the anchor holds, general otherwise), one summary
/// comment with acceptance-criteria verdicts, and set the PAT user's reviewer vote to
/// "waiting for the author" (-5) when anything needs the author's attention.
/// </summary>
public sealed class PublishFindingsStage(IPullRequestSource source, ILogger<PublishFindingsStage> logger) : IReviewStage
{
    public const int VoteWaitingForAuthor = -5;

    public string Name => "publish-findings";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var posted = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var finding in ctx.AcceptedFindings)
        {
            if (finding is {Anchor: not null, AnchorDowngraded: false})
            {
                var threadId = await source.PostFindingThreadAsync(ctx.Pr, finding, ct);
                posted[finding.DedupeKey!] = threadId;
            }
            else
            {
                await source.PostGeneralCommentAsync(ctx.Pr, CommentFormatter.FormatFinding(finding), ct);
            }
        }

        ctx.PostedThreadIds = posted;

        await source.PostGeneralCommentAsync(ctx.Pr,
            CommentFormatter.FormatSummary(ctx.Result!, ctx.WorkItems, ctx.UnansweredThreads, ctx.Kind), ct);

        var acUnmet = (ctx.Result!.Narrative.AcceptanceCriteria ?? []).Any(v => v.Status == AcStatus.Unmet);
        if (ctx.AcceptedFindings.Count > 0 || acUnmet || ctx.UnansweredThreads.Count > 0)
        {
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.CurrentUser!.Id, VoteWaitingForAuthor, ct);
            logger.LogInformation("reviewer vote set to waiting-for-author for {User}", ctx.CurrentUser!.DisplayName);
        }
    }
}