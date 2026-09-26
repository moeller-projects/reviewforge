using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 9: post findings (inline when the anchor holds, general otherwise), one summary
/// comment with acceptance-criteria verdicts, and set the PAT user's reviewer vote to
/// <see cref="ReviewerVote.WaitingForAuthor"/> when anything needs the author's attention,
/// or to the configured clean value on a fully clean run.
/// </summary>
public sealed class PublishFindingsStage(
    IPullRequestSource source,
    IFindingStore store,
    ILogger<PublishFindingsStage> logger,
    ReviewerVote? cleanVote = ReviewerVote.NoResponse) : IReviewStage
{
    /// <summary>Bounded concurrency for ADO writes; each finding is one HTTP round-trip.</summary>
    public const int MaxConcurrentPosts = 4;

    public string Name => "publish-findings";

    public int Order => 90;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // Mechanism B: never re-post a finding that already has a live bot thread. The ADO
        // thread properties (ReviewForge.DedupeKey) are the cross-run source of truth and
        // survive a lost/corrupt local store. Fixed/Closed threads do not suppress — a
        // resolved finding that regresses must be re-posted, EXCEPT when the regression is
        // resurfaced by reopening that thread in triage (P1-11): exactly one visible action.
        var liveThreadKeys = ctx.Threads
            .Where(t => t.DedupeKey is not null
                        && t.Status is ReviewThreadStatus.Active or ReviewThreadStatus.Pending)
            .Select(t => t.DedupeKey!)
            .ToHashSet(StringComparer.Ordinal);

        var regressedThreadIds = ctx.Threads
            .Where(t => t.DedupeKey is not null
                        && ctx.Collector.RegressedKeys.Contains(t.DedupeKey)
                        && t.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
            .ToDictionary(t => t.DedupeKey!, t => t.Id, StringComparer.Ordinal);

        var toPost = ctx.AcceptedFindings
            .Where(f => f.DedupeKey is null
                        || (!liveThreadKeys.Contains(f.DedupeKey) && !regressedThreadIds.ContainsKey(f.DedupeKey)))
            .ToList();

        // Fixed findings with a live bot thread are NOT suppressed: the fix lands as a
        // reply on the existing thread (Mechanism B would otherwise swallow it).
        var liveFixedReplies = ctx.AcceptedFindings
            .Where(f => f.AppliedFix is not null
                        && f.DedupeKey is not null
                        && liveThreadKeys.Contains(f.DedupeKey)
                        && !regressedThreadIds.ContainsKey(f.DedupeKey))
            .Select(f => (ThreadId: ctx.Threads.First(t => t.DedupeKey == f.DedupeKey).Id,
                          Body: CommentFormatter.FormatFixedFinding(f)))
            .ToList();

        foreach (var suppressed in ctx.AcceptedFindings.Where(f => f.DedupeKey is not null && liveThreadKeys.Contains(f.DedupeKey)))
        {
            logger.LogInformation("suppressing finding {Key}: live bot thread already exists", suppressed.DedupeKey);
        }

        foreach (var finding in ctx.AcceptedFindings)
        {
            if (finding.DedupeKey is { } key && regressedThreadIds.TryGetValue(key, out var threadId))
            {
                logger.LogInformation("suppressing finding {Key}: regressed thread {ThreadId} is reopened by triage instead", key, threadId);
            }
        }

        PublishGuardChecks.ThrowIfClaimLost(ctx, "before publish");

        // Head-SHA TOCTOU guard: the checkout, diff, and anchors were computed against the
        // stage-1 head. A force-push mid-run must not receive comments for superseded code.
        var current = await source.GetPullRequestAsync(ctx.Pr, ct).ConfigureAwait(false);
        var reviewed = ctx.RequirePullRequest().SourceCommitSha;
        if (!string.Equals(current.SourceCommitSha, reviewed, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("PR head changed during run ({Reviewed} → {Current}); aborting before publication",
                reviewed, current.SourceCommitSha);
            throw new PrHeadChangedException(reviewed, current.SourceCommitSha);
        }

        var posted = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, threadId) in regressedThreadIds)
        {
            // The finding is not re-posted (triage reopens the thread); stamp the existing
            // thread id so persistence keeps the key → thread mapping intact (P1-11).
            posted[key] = threadId;
        }

        using var gate = new SemaphoreSlim(MaxConcurrentPosts, MaxConcurrentPosts);

        var inlineTasks = toPost
            .Where(f => f is {Anchor: not null, AnchorDowngraded: false})
            .Select(async finding =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    PublishGuardChecks.ThrowIfClaimLost(ctx, "during publish");
                    // A fixed finding is anchored at the FIX range: ADO applies a suggestion
                    // block to the thread's anchored range.
                    var toPublish = finding.AppliedFix is { } fix
                        ? finding with
                        {
                            Anchor = new FindingAnchor(
                                fix.Proposal.FilePath, fix.Proposal.StartLine, fix.Proposal.EndLine)
                        }
                        : finding;
                    var threadId = await source.PostFindingThreadAsync(ctx.Pr, toPublish, ct).ConfigureAwait(false);
                    posted[finding.DedupeKey!] = threadId;
                    ReviewForgeTelemetry.FindingsPosted.Add(1, new TagList { { "kind", "inline" } });
                    if (finding.AppliedFix is { } applied)
                    {
                        ReviewForgeTelemetry.FixesApplied.Add(1, FixTags(FixOrigin.Deterministic, finding.RuleId));
                    }
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
                    PublishGuardChecks.ThrowIfClaimLost(ctx, "during publish");
                    await source.PostGeneralCommentAsync(
                            ctx.Pr, CommentFormatter.FormatFinding(finding), finding.DedupeKey, ct)
                        .ConfigureAwait(false);
                    ReviewForgeTelemetry.FindingsPosted.Add(1, new TagList { { "kind", "general" } });
                }
                finally
                {
                    gate.Release();
                }
            });

        await Task.WhenAll(inlineTasks.Concat(generalTasks)).ConfigureAwait(false);

        // Live-thread fixed findings: reply with the fix body (never suppressed, never
        // re-posted as a new thread).
        foreach (var (threadId, body) in liveFixedReplies)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix reply on thread {threadId}");
            await source.ReplyToThreadAsync(ctx.Pr, threadId, body, ct).ConfigureAwait(false);
            ReviewForgeTelemetry.ThreadsReplied.Add(1);
            ReviewForgeTelemetry.FixesApplied.Add(1, FixTags(FixOrigin.Deterministic, "existing-thread"));
        }

        // Commanded fixes: a new suggestion thread WITHOUT a dedupe property (invisible
        // to triage and publish suppression), plus a link reply on the command thread.
        foreach (var fix in ctx.AppliedFixes.Where(f => f.Proposal.SourceThreadId is not null))
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before fix suggestion");
            var commandThreadId = fix.Proposal.SourceThreadId!.Value;
            var anchor = new ThreadAnchor(
                fix.Proposal.FilePath, fix.Proposal.StartLine, fix.Proposal.EndLine);
            var excerpt = ctx.FixCommands.FirstOrDefault(c => c.ThreadId == commandThreadId)?.QuotedComment
                          ?? string.Empty;
            await source.PostSuggestionThreadAsync(
                    ctx.Pr, anchor, CommentFormatter.FormatFixedFinding(fix.Proposal, excerpt), ct)
                .ConfigureAwait(false);
            var link = $"Fix posted above ⤴ (suggestion for {fix.Proposal.FilePath}:{fix.Proposal.StartLine}–{fix.Proposal.EndLine}).";
            await source.ReplyToThreadAsync(ctx.Pr, commandThreadId, CommentFormatter.WithBotPreamble(link), ct)
                .ConfigureAwait(false);
            ReviewForgeTelemetry.ThreadsReplied.Add(1);
            ReviewForgeTelemetry.FixesApplied.Add(1, FixTags(FixOrigin.LlmCommanded, "thread-command"));
        }

        // Replies the auto-fix stage queued (declines, verifier failures, exhausted budget).
        foreach (var (threadId, text) in ctx.FixCommandReplies)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix command reply on thread {threadId}");
            var body = CommentFormatter.WithBotPreamble(text);
            if (await AlreadyRepliedAsync(ctx, threadId, body, ct).ConfigureAwait(false))
            {
                logger.LogInformation("thread {ThreadId}: fix reply already posted by a previous attempt — skipping", threadId);
                continue;
            }

            await source.ReplyToThreadAsync(ctx.Pr, threadId, body, ct).ConfigureAwait(false);
            ReviewForgeTelemetry.ThreadsReplied.Add(1);
        }

        ctx.PostedThreadIds = posted;

        // Summary must post AFTER findings (readers of the PR see findings first).
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before summary");
        await source.PostGeneralCommentAsync(
                ctx.Pr,
                CommentFormatter.FormatSummary(
                    ctx.RequireResult(), ctx.WorkItems, ctx.UnansweredThreads, ctx.Kind,
                    appliedFixCount: ctx.AppliedFixes.Count),
                dedupeKey: null,
                ct: ct)
            .ConfigureAwait(false);

        var acUnmet = (ctx.RequireResult().Narrative.AcceptanceCriteria ?? []).Any(v => v.Status == AcStatus.Unmet);
        var needsAttention = ctx.AcceptedFindings.Count > 0 || acUnmet || ctx.UnansweredThreads.Count > 0;
        if (needsAttention)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before vote");
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.CurrentUser!.Id, ReviewerVote.WaitingForAuthor, ct);
            logger.LogInformation("reviewer vote set to waiting-for-author for {User}", ctx.CurrentUser!.DisplayName);
        }
        else if (cleanVote is { } vote)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before vote");
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.CurrentUser!.Id, vote, ct);
            logger.LogInformation("clean run: reviewer vote reset to {Vote} for {User}", vote, ctx.CurrentUser!.DisplayName);
        }
    }

    private static TagList FixTags(FixOrigin origin, string rule)
        => new() { {"origin", origin.ToString().ToLowerInvariant()}, {"rule", rule} };

    /// <summary>
    /// Re-fetch the thread immediately before replying: a previous attempt or a
    /// competing run may have posted this exact reply after ctx.Threads was fetched.
    /// </summary>
    private async Task<bool> AlreadyRepliedAsync(ReviewContext ctx, int threadId, string text, CancellationToken ct)
    {
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        return threads.FirstOrDefault(t => t.Id == threadId)?.LastComment is {IsBot: true} last
               && string.Equals(last.Text.Trim(), text.Trim(), StringComparison.Ordinal);
    }
}