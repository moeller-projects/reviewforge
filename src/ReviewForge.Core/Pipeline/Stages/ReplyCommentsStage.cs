using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class ReplyCommentsStage(
    IPullRequestSource source,
    IFindingStore store,
    bool setFixedStatus,
    ILogger<ReplyCommentsStage>? logger = null) : IReviewStage
{
    public string Name => "reply-comments";
    public int Order => 80;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var unresolved = await store.GetResolveActionsAsync(ctx.Pr, [], ct).ConfigureAwait(false);
        var actions = unresolved.Where(a => !a.ReplyPosted).OrderBy(a => a.RunId == ctx.RunId ? 1 : 0).ThenBy(a => a.Id).ToArray();
        var threads = ctx.Threads.ToDictionary(t => t.Id);
        var applied = ctx.AppliedResolutions.ToDictionary(r => r.ThreadId);
        var replies = 0;
        foreach (var pendingAction in actions)
        {
            var action = pendingAction;
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before resolve reply on thread {action.ThreadId}");
            var body = action.ReplyText ?? CommentFormatter.WithBotPreamble(FormatOutcome(ctx, action, applied));
            if (action.ReplyText is null)
            {
                action = action with { ReplyText = body };
                await store.SaveResolveActionsAsync(ctx.Pr, action.RunId, [action], ct).ConfigureAwait(false);
            }
            if (threads.TryGetValue(action.ThreadId, out var thread)
                && thread.Comments.LastOrDefault() is { IsBot: true } last
                && string.Equals(last.Text, body, StringComparison.Ordinal))
            {
                if (setFixedStatus && action.Outcome == ResolutionOutcome.Fixed)
                    await source.SetThreadStatusAsync(ctx.Pr, action.ThreadId, ReviewThreadStatus.Fixed, ct).ConfigureAwait(false);
                ReviewForgeTelemetry.ResolveRepliesDeduped.Add(1, new TagList { { "outcome", action.Outcome.ToString().ToLowerInvariant() } });
                await store.MarkResolveActionRepliedAsync(action.Id, ct).ConfigureAwait(false);
                continue;
            }
            await source.ReplyToThreadAsync(ctx.Pr, action.ThreadId, body, ct).ConfigureAwait(false);
            if (setFixedStatus && action.Outcome == ResolutionOutcome.Fixed)
                await source.SetThreadStatusAsync(ctx.Pr, action.ThreadId, ReviewThreadStatus.Fixed, ct).ConfigureAwait(false);
            await store.MarkResolveActionRepliedAsync(action.Id, ct).ConfigureAwait(false);
            ReviewForgeTelemetry.ResolveRepliesPosted.Add(1, new TagList { { "outcome", action.Outcome.ToString().ToLowerInvariant() } });
            replies++;
        }

        var current = unresolved.Where(a => a.RunId == ctx.RunId).ToArray();
        var summaryKey = $"resolve-{ctx.RunId:D}";
        if (current.Length > 0 && !threads.Values.Any(t => t.DedupeKey == summaryKey))
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before resolve summary");
            var summary = new StringBuilder(CommentFormatter.BotPreamble).AppendLine().AppendLine()
                .AppendLine("## ReviewForge · resolve summary");
            foreach (var group in current.Where(a => a.CommitSha is not null)
                         .GroupBy(a => a.CommitSha!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var subject = ctx.AppliedResolutions.FirstOrDefault(item => item.CommitSha == group.Key)?.CommitSubject;
                summary.Append("- Commit: ").Append(group.Key[..Math.Min(7, group.Key.Length)]);
                if (!string.IsNullOrWhiteSpace(subject)) summary.Append(" — ").Append(subject);
                summary.AppendLine();
            }
            var deferred = current.Where(a => a.Outcome == ResolutionOutcome.Deferred).Select(a => $"#{a.ThreadId}").ToArray();
            if (deferred.Length > 0) summary.Append("- Deferred: ").AppendLine(string.Join(", ", deferred));
            summary.Append("- Verification: ").AppendLine(ctx.ResolveVerificationStatus);
            await source.PostGeneralCommentAsync(ctx.Pr, summary.ToString().TrimEnd(), summaryKey, ct).ConfigureAwait(false);
        }
        logger?.LogInformation("resolve replies posted: {Count}", replies);
    }

    private static string FormatOutcome(ReviewContext ctx, ResolveAction action, IReadOnlyDictionary<int, AppliedResolution> applied)
    {
        var evidence = ctx.ThreadVerdicts.FirstOrDefault(v => v.ThreadId == action.ThreadId)?.Evidence;
        var detail = ctx.ResolutionDetails.GetValueOrDefault(action.ThreadId);
        return action.Outcome switch
        {
            ResolutionOutcome.Fixed when action.CommitSha is { } sha => $"Fixed in {sha[..Math.Min(7, sha.Length)]} — {applied.GetValueOrDefault(action.ThreadId)?.CommitSubject ?? "applied the requested correction"}. {detail}",
            ResolutionOutcome.AgentDeclined => $"I looked into this — {detail ?? "I could not identify a safe change"}. No change made.",
            ResolutionOutcome.NonIssue => $"I don't think this is an issue: {evidence ?? "the current code does not reproduce the concern"}. Happy to revisit if I'm missing context.",
            ResolutionOutcome.Question => ctx.ThreadVerdicts.FirstOrDefault(v => v.ThreadId == action.ThreadId)?.Answer ?? evidence ?? "I need more context to answer this question.",
            ResolutionOutcome.AlreadyFixed => $"This already holds on {ctx.RequirePullRequest().SourceCommitSha[..Math.Min(7, ctx.RequirePullRequest().SourceCommitSha.Length)]}: {evidence ?? "the current head contains the requested behavior"}.",
            ResolutionOutcome.PushFailed => "The resolution could not be published; no change from this fix was published.",
            ResolutionOutcome.Deferred => $"This run's budget is exhausted; re-queued for the next resolve run.",
            _ => $"I can't safely act on this one: {detail ?? evidence ?? "needs a human decision"} — needs a human decision."
        };
    }
}
