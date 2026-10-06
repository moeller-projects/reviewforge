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
    ReviewerVote? cleanVote = ReviewerVote.NoResponse,
    AutoFixOptions? autoFix = null) : IReviewStage
{
    /// <summary>Bounded concurrency for ADO writes; each finding is one HTTP round-trip.</summary>
    public const int MaxConcurrentPosts = 4;

    public string Name => "publish-findings";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        // Cancelled-before-first-write: a shutdown requested during reasoning must not
        // leave a partial publish behind. From here to the end the stage finishes its
        // dedupe-safe write set even under shutdown cancellation — findings without their
        // summary, or a reply without its status, are worse than a slightly delayed
        // shutdown. The AlreadyReplied/SuggestionAlreadyPosted checks make a re-run safe,
        // but a clean run is cheaper than a recovered one. Claim guards stay live for
        // every write; the remaining work is bounded (one HTTP round-trip per write).
        ct.ThrowIfCancellationRequested();
        ct = CancellationToken.None;

        var pushedFixRowIds = new Dictionary<string, int>(StringComparer.Ordinal);

        // CommitOnHead reconciliation FIRST: pushed-fix rows from PRIOR runs (crash orphans —
        // the push landed but its replies never did) get their "Fixed in {sha}" replies now,
        // constructed entirely from the durable record. Rows of THIS run flow through the
        // normal paths below and are marked replied as each reply lands.
        if (autoFix is { IsCommitOnHead: true })
        {
            // Reconciliation is a publish write path too: validate the run's reviewed head
            // (or its own pushed head) before any orphan reply is attempted.
            await EnsureHeadUnchangedAsync(ctx, ct).ConfigureAwait(false);
            foreach (var fix in await store.GetUnrepliedPushedFixesAsync(ctx.Pr, ct).ConfigureAwait(false))
            {
                if (fix.RunId == ctx.RunId)
                {
                    pushedFixRowIds[fix.DedupeKey] = fix.Id;
                    continue;
                }

                await ReconcilePushedFixAsync(ctx, fix, ct).ConfigureAwait(false);
            }
        }

        // Mechanism B: never re-post a finding that already has a live bot thread. The ADO
        // thread properties (ReviewForge.DedupeKey) are the cross-run source of truth and
        // survive a lost/corrupt local store. Fixed/Closed threads do not suppress — a
        // resolved finding that regresses must be re-posted, EXCEPT when the regression is
        // resurfaced by reopening that thread in triage (P1-11): exactly one visible action.
        var liveThreadKeys = ctx.Fetch.Threads
            .Where(t => t.DedupeKey is not null
                        && t.Status is ReviewThreadStatus.Active or ReviewThreadStatus.Pending)
            .Select(t => t.DedupeKey!)
            .ToHashSet(StringComparer.Ordinal);

        var regressedThreadIds = ctx.Fetch.Threads
            .Where(t => t.DedupeKey is not null
                        && ctx.Reasoning.Collector.RegressedKeys.Contains(t.DedupeKey)
                        && t.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
            .ToDictionary(t => t.DedupeKey!, t => t.Id, StringComparer.Ordinal);

        var toPost = ctx.Validation.AcceptedFindings
            .Where(f => f.DedupeKey is null
                        || (!liveThreadKeys.Contains(f.DedupeKey) && !regressedThreadIds.ContainsKey(f.DedupeKey)))
            .ToList();

        // Fixed findings with a live bot thread are NOT suppressed: the fix lands as a
        // reply on the existing thread (Mechanism B would otherwise swallow it).
        var liveFixedReplies = ctx.Validation.AcceptedFindings
            .Where(f => f.AppliedFix is not null
                        && f.DedupeKey is not null
                        && liveThreadKeys.Contains(f.DedupeKey)
                        && !regressedThreadIds.ContainsKey(f.DedupeKey))
            .Select(f => (Key: f.DedupeKey!,
                          ThreadId: ctx.Fetch.Threads.First(t => t.DedupeKey == f.DedupeKey).Id,
                          Body: CommentFormatter.FormatFixedFinding(f, f.AppliedFix!)))
            .ToList();

        foreach (var suppressed in ctx.Validation.AcceptedFindings.Where(f => f.DedupeKey is not null && liveThreadKeys.Contains(f.DedupeKey)))
        {
            logger.LogInformation("suppressing finding {Key}: live bot thread already exists", suppressed.DedupeKey);
        }

        foreach (var finding in ctx.Validation.AcceptedFindings)
        {
            if (finding.DedupeKey is { } key && regressedThreadIds.TryGetValue(key, out var threadId))
            {
                logger.LogInformation("suppressing finding {Key}: regressed thread {ThreadId} is reopened by triage instead", key, threadId);
            }
        }

        PublishGuardChecks.ThrowIfClaimLost(ctx, "before publish");

        // Head-SHA TOCTOU guard: the checkout, diff, and anchors were computed against the
        // stage-1 head. A force-push mid-run must not receive comments for superseded code.
        await EnsureHeadUnchangedAsync(ctx, ct).ConfigureAwait(false);

        var publishedFixCount = 0;
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
                    FindingsTelemetry.FindingsPosted.Add(1, new TagList { { "kind", "inline" } });
                    if (finding.AppliedFix is { } applied)
                    {
                        Interlocked.Increment(ref publishedFixCount);
                        AutoFixTelemetry.FixesApplied.Add(
                            1, FixTags(applied.Proposal.Origin, finding.RuleId));
                    }
                    if (finding.AppliedFix?.CommitSha is not null
                        && pushedFixRowIds.TryGetValue(finding.DedupeKey!, out var inlineRowId))
                    {
                        // The commit announcement WAS the finding body — the row is replied.
                        await store.MarkPushedFixRepliedAsync(inlineRowId, ct).ConfigureAwait(false);
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
                    var body = CommentFormatter.FormatFinding(finding);
                    var committedRowId = finding.AppliedFix?.CommitSha is not null
                                         && finding.DedupeKey is { } key
                                         && pushedFixRowIds.TryGetValue(key, out var rowId)
                        ? rowId
                        : (int?)null;
                    if (committedRowId is { } existingRowId
                        && await AlreadyPostedGeneralAsync(ctx, finding.DedupeKey!, body, ct).ConfigureAwait(false))
                    {
                        await store.MarkPushedFixRepliedAsync(existingRowId, ct).ConfigureAwait(false);
                        return;
                    }

                    PublishGuardChecks.ThrowIfClaimLost(ctx, "before general finding");
                    await source.PostGeneralCommentAsync(ctx.Pr, body, finding.DedupeKey, ct)
                        .ConfigureAwait(false);
                    FindingsTelemetry.FindingsPosted.Add(1, new TagList { { "kind", "general" } });
                    if (committedRowId is { } postedRowId)
                    {
                        await store.MarkPushedFixRepliedAsync(postedRowId, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

        await Task.WhenAll(inlineTasks.Concat(generalTasks)).ConfigureAwait(false);

        // Re-check immediately before the sequential auto-fix writes. The initial check
        // protects the ordinary finding posts; this one closes the TOCTOU window before
        // live replies, commanded suggestions, and queued command replies.
        await EnsureHeadUnchangedAsync(ctx, ct).ConfigureAwait(false);

        // Live-thread fixed findings: reply with the fix body (never suppressed, never
        // re-posted as a new thread). Committed fixes (CommitOnHead) mark their pushed-fix
        // row replied — including the already-posted skip path (exactly-once convergence).
        foreach (var (key, threadId, body) in liveFixedReplies)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix reply on thread {threadId}");
            if (await AlreadyRepliedAsync(ctx, threadId, body, ct).ConfigureAwait(false))
            {
                logger.LogInformation("thread {ThreadId}: fix reply already posted by a previous attempt — skipping", threadId);
                if (pushedFixRowIds.TryGetValue(key, out var skippedRowId))
                {
                    await store.MarkPushedFixRepliedAsync(skippedRowId, ct).ConfigureAwait(false);
                }

                continue;
            }

            await source.ReplyToThreadAsync(ctx.Pr, threadId, body, ct).ConfigureAwait(false);
            ReviewTelemetry.ThreadsReplied.Add(1);
            AutoFixTelemetry.FixesApplied.Add(1, FixTags(FixOrigin.Deterministic, "existing-thread"));
            Interlocked.Increment(ref publishedFixCount);
            if (pushedFixRowIds.TryGetValue(key, out var rowId))
            {
                await store.MarkPushedFixRepliedAsync(rowId, ct).ConfigureAwait(false);
            }
        }

        // Commanded fixes: a new suggestion thread WITHOUT a dedupe property (invisible
        // to triage and publish suppression), plus a link reply on the command thread.
        // CommitOnHead: committed commanded fixes skip this entirely — stage 10 queued a
        // "Fixed in {sha}" reply instead of a suggestion.
        foreach (var fix in ctx.AutoFix.AppliedFixes.Where(f => f.Proposal.SourceThreadId is not null && f.CommitSha is null))
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before fix suggestion");
            var commandThreadId = fix.Proposal.SourceThreadId!.Value;
            var anchor = new ThreadAnchor(
                fix.Proposal.FilePath, fix.Proposal.StartLine, fix.Proposal.EndLine);
            var excerpt = ctx.AutoFix.FixCommands.FirstOrDefault(c => c.ThreadId == commandThreadId)?.QuotedComment
                          ?? string.Empty;
            var body = CommentFormatter.FormatFixedFinding(fix.Proposal, excerpt);
            if (await SuggestionAlreadyPostedAsync(ctx, anchor, body, ct).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "command thread {ThreadId}: matching suggestion already posted — skipping",
                    commandThreadId);
                continue;
            }

            await source.PostSuggestionThreadAsync(ctx.Pr, anchor, body, ct)
                .ConfigureAwait(false);
            await PersistCommandAuditAsync(ctx, ct).ConfigureAwait(false);
            Interlocked.Increment(ref publishedFixCount);
            AutoFixTelemetry.FixesApplied.Add(1, FixTags(FixOrigin.LlmCommanded, "thread-command"));
            // This guard deliberately sits after the awaited suggestion write and directly
            // before the link reply, so a lost claim cannot add a second command-thread write.
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix command link on thread {commandThreadId}");
            var link = $"Fix posted above ⤴ (suggestion for {fix.Proposal.FilePath}:{fix.Proposal.StartLine}–{fix.Proposal.EndLine}).";
            await source.ReplyToThreadAsync(ctx.Pr, commandThreadId, CommentFormatter.WithBotPreamble(link), ct)
                .ConfigureAwait(false);
            ReviewTelemetry.ThreadsReplied.Add(1);
        }

        // Replies the auto-fix stage queued (declines, verifier failures, exhausted budget)
        // and, in CommitOnHead mode, stage 10's "Fixed in {sha}" replies. A reply that has a
        // pushed-fix row ("thread-{id}" key) marks it replied — including the already-posted
        // skip path (exactly-once convergence).
        foreach (var (threadId, text) in ctx.AutoFix.FixCommandReplies)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix command reply on thread {threadId}");
            var body = CommentFormatter.WithBotPreamble(text);
            var markedRowId = pushedFixRowIds.TryGetValue($"{AppliedFix.CommandKeyPrefix}{threadId}", out var commandRowId)
                ? commandRowId
                : (int?)null;
            if (await AlreadyRepliedAsync(ctx, threadId, body, ct).ConfigureAwait(false))
            {
                logger.LogInformation("thread {ThreadId}: fix reply already posted by a previous attempt — skipping", threadId);
                if (markedRowId is { } skippedRowId)
                {
                    await store.MarkPushedFixRepliedAsync(skippedRowId, ct).ConfigureAwait(false);
                }

                continue;
            }

            await source.ReplyToThreadAsync(ctx.Pr, threadId, body, ct).ConfigureAwait(false);
            ReviewTelemetry.ThreadsReplied.Add(1);
            if (markedRowId is { } rowId)
            {
                await store.MarkPushedFixRepliedAsync(rowId, ct).ConfigureAwait(false);
            }
        }
        ctx.Published = ctx.Published with { PostedThreadIds = posted };

        // Summary must post AFTER findings (readers of the PR see findings first).
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before summary");
        await source.PostGeneralCommentAsync(
                ctx.Pr,
                CommentFormatter.FormatSummary(
                    ctx.RequireResult(), ctx.Fetch.WorkItems, ctx.Triage.UnansweredThreads, ctx.Classification.Kind,
                    appliedFixCount: publishedFixCount),
                dedupeKey: null,
                ct: ct)
            .ConfigureAwait(false);

        var acUnmet = (ctx.RequireResult().Narrative.AcceptanceCriteria ?? []).Any(v => v.Status == AcStatus.Unmet);
        var needsAttention = ctx.Validation.AcceptedFindings.Count > 0 || acUnmet || ctx.Triage.UnansweredThreads.Count > 0;
        if (needsAttention)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before vote");
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.Fetch.CurrentUser!.Id, ReviewerVote.WaitingForAuthor, ct);
            logger.LogInformation("reviewer vote set to waiting-for-author for {User}", ctx.Fetch.CurrentUser!.DisplayName);
        }
        else if (cleanVote is { } vote)
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before vote");
            await source.SetReviewerVoteAsync(ctx.Pr, ctx.Fetch.CurrentUser!.Id, vote, ct);
            logger.LogInformation("clean run: reviewer vote reset to {Vote} for {User}", vote, ctx.Fetch.CurrentUser!.DisplayName);
        }
    }

    /// <summary>
    /// Crash-after-push recovery: a pushed-fix row from a PRIOR run whose reply never landed.
    /// The reply is constructed entirely from the durable record (SHA + subject; the live
    /// thread via the ReviewForge.DedupeKey thread property, or the stored command thread).
    /// Dedupe recognizes BOTH announcement forms the crashed run may have used — the short
    /// "Fixed in {sha7}" reply and the full committed-finding body (which embeds the same
    /// line) — so a crash between the original announcement and mark-replied converges
    /// without posting a second, duplicate comment.
    /// </summary>
    private async Task ReconcilePushedFixAsync(ReviewContext ctx, PushedFix fix, CancellationToken ct)
    {
        var body = CommentFormatter.FormatCommittedFixReply(fix.CommitSha, fix.CommitSubject, fix.AiDrafted);
        var marker = CommentFormatter.CommittedFixLine(fix.CommitSha, fix.CommitSubject, fix.AiDrafted);
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        var liveThreadId = threads
            .FirstOrDefault(t =>
                (t.Status is ReviewThreadStatus.Active or ReviewThreadStatus.Pending)
                && (t.Id == fix.ThreadId || t.DedupeKey == fix.DedupeKey))
            ?.Id;

        if (liveThreadId is null)
        {
            // A closed/missing thread cannot receive a reply. Its general-comment fallback
            // carries the dedupe key, so a crash between post and mark converges.
            PublishGuardChecks.ThrowIfClaimLost(ctx, "before reconciled fix comment");
            if (threads.Any(t => t.DedupeKey == fix.DedupeKey && AnnouncementExists(t, body, marker)))
            {
                await store.MarkPushedFixRepliedAsync(fix.Id, ct).ConfigureAwait(false);
                return;
            }

            PublishGuardChecks.ThrowIfClaimLost(ctx, "before reconciled fix comment");
            await source.PostGeneralCommentAsync(ctx.Pr, body, fix.DedupeKey, ct).ConfigureAwait(false);
        }
        else
        {
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before reconciled fix reply on thread {liveThreadId}");
            if (AnnouncementExists(threads.First(t => t.Id == liveThreadId.Value), body, marker))
            {
                logger.LogInformation(
                    "pushed fix {Key}: announcement already posted by the crashed run — marking replied", fix.DedupeKey);
                await store.MarkPushedFixRepliedAsync(fix.Id, ct).ConfigureAwait(false);
                return;
            }

            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before reconciled fix reply on thread {liveThreadId}");
            await source.ReplyToThreadAsync(ctx.Pr, liveThreadId.Value, body, ct).ConfigureAwait(false);
            ReviewTelemetry.ThreadsReplied.Add(1);
        }

        await store.MarkPushedFixRepliedAsync(fix.Id, ct).ConfigureAwait(false);
        AutoFixTelemetry.AutoFixReconciledReplies.Add(1);
        logger.LogInformation(
            "pushed fix {Key} ({Sha}): reconciled missing reply from run {RunId}",
            fix.DedupeKey, fix.CommitSha, fix.RunId);
    }

    /// <summary>True when any bot comment on the thread is either the short reconciliation
    /// reply or the full committed-finding body — both embed the "Fixed in {sha7}" marker.</summary>
    private static bool AnnouncementExists(ReviewThread thread, string body, string marker)
        => thread.Comments.Any(c =>
            c.IsBot
            && (string.Equals(c.Text.Trim(), body.Trim(), StringComparison.Ordinal)
                || c.Text.Contains(marker, StringComparison.Ordinal)));
    private static TagList FixTags(FixOrigin origin, string rule)
        => new() { {"origin", origin.ToString().ToLowerInvariant()}, {"rule", rule} };
    private Task PersistCommandAuditAsync(ReviewContext ctx, CancellationToken ct)
    {
        var findings = AppliedFixPersistence.BuildFinalRows(
            ctx, key => ctx.Published.PostedThreadIds.TryGetValue(key, out var id) ? id : null);
        var pr = ctx.RequirePullRequest();
        return store.SaveRunAsync(
            new ReviewRun(
                ctx.RunId, ctx.Pr, pr.SourceCommitSha, ctx.Classification.Kind,
                ctx.StartedAt, CompletedAt: null, Success: false, findings),
            ct);
    }

    private async Task EnsureHeadUnchangedAsync(ReviewContext ctx, CancellationToken ct)
    {
        var current = await source.GetPullRequestAsync(ctx.Pr, ct).ConfigureAwait(false);
        // CommitOnHead: the run's own push (stage 10) is the one legal head movement —
        // publish happens against the pushed head. Any movement BEYOND it still fails the run.
        var reviewed = ctx.AutoFix.PushedHeadSha ?? ctx.RequirePullRequest().SourceCommitSha;
        if (!string.Equals(current.SourceCommitSha, reviewed, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("PR head changed during run ({Reviewed} → {Current}); aborting before publication",
                reviewed, current.SourceCommitSha);
            throw new PrHeadChangedException(reviewed, current.SourceCommitSha);
        }
    }

    private async Task<bool> SuggestionAlreadyPostedAsync(
        ReviewContext ctx, ThreadAnchor anchor, string body, CancellationToken ct)
    {
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        return threads.Any(thread =>
            thread.Anchor is { } existingAnchor
            && existingAnchor == anchor
            && thread.Comments.FirstOrDefault() is {IsBot: true} first
            && string.Equals(first.Text.Trim(), body.Trim(), StringComparison.Ordinal));
    }
    private async Task<bool> AlreadyPostedGeneralAsync(
        ReviewContext ctx, string dedupeKey, string text, CancellationToken ct)
    {
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        return threads.Any(t =>
            t.DedupeKey == dedupeKey
            && t.LastComment is { IsBot: true } last
            && string.Equals(last.Text.Trim(), text.Trim(), StringComparison.Ordinal));
    }


    /// <summary>
    /// Re-fetch the thread immediately before replying: a previous attempt or a
    /// competing run may have posted this exact reply after ctx.Fetch.Threads was fetched.
    /// </summary>
    private async Task<bool> AlreadyRepliedAsync(ReviewContext ctx, int threadId, string text, CancellationToken ct)
    {
        var threads = await source.GetThreadsAsync(ctx.Pr, ct).ConfigureAwait(false);
        return threads.FirstOrDefault(t => t.Id == threadId)?.LastComment is {IsBot: true} last
               && string.Equals(last.Text.Trim(), text.Trim(), StringComparison.Ordinal);
    }
}