using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 10 (after begin-run, before triage): commits the fixes stage 8 materialized in
/// Push is the run's point of no return: the claim is re-checked immediately before it, the
/// remote tip must equal the reviewed head at the pre-read, and the server-side
/// non-fast-forward rejection is the compare-and-swap for any movement after that. The
/// pushed-fix rows are persisted IMMEDIATELY after a successful push — before this stage
/// returns and before publish runs — so a crash between push and replies is reconciled by a
/// later run's publish stage from the durable record alone. Placement after BeginRunStage
/// guarantees the in-flight shell exists before the first external write; pushed commits
/// carry the ReviewForge-Run trailer naming that row (the loop guard's suppression signal).
/// </summary>
public sealed class CommitFixesStage(
    IGitOps git,
    IFindingStore store,
    AutoFixOptions options,
    ILogger<CommitFixesStage> logger,
    string? pat = null) : IReviewStage
{
    public string Name => "commit-fixes";


    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (!options.IsCommitOnHead)
        {
            return;
        }

        var pending = ctx.AutoFix.AppliedFixes.Where(f => f.AppliedToTree).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var pr = ctx.RequirePullRequest();

        // Eligibility pre-check BEFORE any commit: without a usable source ref every fix
        // degrades to suggestion-mode publication; the run continues.
        const string headsPrefix = "refs/heads/";
        if (pr.SourceRefName is not { } refName
            || !refName.StartsWith(headsPrefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(refName[headsPrefix.Length..]))
        {
            logger.LogWarning(
                "commit-fixes: SourceRefName '{SourceRefName}' is not a {Prefix} ref — all {Count} fixes degrade to suggestion mode",
                pr.SourceRefName ?? "(null)", headsPrefix, pending.Count);
            AutoFixTelemetry.AutoFixDegradedToSuggestion.Add(
                pending.Count, new TagList {{ReviewForgeTelemetry.TagReason, "no_source_ref"}});
            return;
        }

        var branch = refName[headsPrefix.Length..];
        var repoDir = ctx.RequireRepoDir();

        PublishGuardChecks.ThrowIfClaimLost(ctx, "before commit");

        var groups = Group(pending).ToArray();
        var findingsByKey = ctx.Validation.AcceptedFindings
            .Where(f => f.DedupeKey is not null)
            .GroupBy(f => f.DedupeKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var committed = new List<(IReadOnlyList<AppliedFix> Fixes, string Sha, string Subject)>();
        var degraded = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var message = ConventionalCommitBuilder.Build(
                ctx.RunId,
                [
                    .. group.Select(f => new ConventionalCommitBuilder.CommitFixInput(
                        f,
                        f.DedupeKey is not null && findingsByKey.TryGetValue(f.DedupeKey, out var finding) ? finding : null))
                ]);
            string sha;
            try
            {
                sha = await git.CommitAsync(
                        repoDir, message,
                        options.CommitAuthorName!, options.CommitAuthorEmail!,
                        paths: [.. group.Select(f => f.Proposal.FilePath).Distinct(RepoPath.PathComparer)],
                        ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("nothing to commit", StringComparison.Ordinal))
            {
                // No fixes in this group survived application (e.g. reverted edits) — degrade
                // the group and continue with the remaining groups.
                logger.LogWarning(
                    "commit-fixes: nothing to commit for {Files} — {Count} fixes degrade to suggestion mode",
                    string.Join(", ", group.Select(f => f.Proposal.FilePath).Distinct(RepoPath.PathComparer)), group.Count);
                degraded += group.Count;
                continue;
            }

            var subject = ConventionalCommitBuilder.SubjectOf(message);
            foreach (var fix in group)
            {
                fix.CommitSha = sha;
                fix.CommitSubject = subject;
            }

            committed.Add((group, sha, subject));
            AutoFixTelemetry.AutoFixCommits.Add(
                1, new TagList {{"granularity", options.CommitGranularity}});
        }

        if (degraded > 0)
        {
            AutoFixTelemetry.AutoFixDegradedToSuggestion.Add(
                degraded, new TagList {{ReviewForgeTelemetry.TagReason, "commit_failed"}});
        }

        if (committed.Count == 0)
        {
            // Everything degraded; publish handles a pure-suggestion run.
            AutoFixTelemetry.AutoFixDegradedToSuggestion.Add(
                1, new TagList {{ReviewForgeTelemetry.TagReason, "all_degraded"}});
            return;
        }

        // The final claim check, immediately before the irreversible step.
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before push");

        // Head pin pre-read (error precision): the server-side non-fast-forward rejection in
        // PushAsync is the CAS for any movement between this read and the ref update.
        var tip = await git.GetRemoteTipAsync(repoDir, pr.CloneUrl, branch, pat, ct).ConfigureAwait(false);
        if (!string.Equals(tip, pr.SourceCommitSha, StringComparison.OrdinalIgnoreCase))
        {
            AutoFixTelemetry.AutoFixPushFailures.Add(
                1, new TagList {{ReviewForgeTelemetry.TagReason, "pin"}});
            throw new PrHeadChangedException(pr.SourceCommitSha, tip ?? "(branch missing on remote)");
        }

        // The claim can be lost during the asynchronous tip read above; re-check it at the
        // irreversible boundary itself, directly before the push.
        PublishGuardChecks.ThrowIfClaimLost(ctx, "at push");

        // Durable push intent BEFORE the external mutation: the rows are invisible to reply
        // reconciliation until confirmed, but a crash after the push (before confirm) leaves a
        // recoverable record of exactly which commits were sent.
        var rows = committed
            .SelectMany(c => c.Fixes.Select(f => new PushedFix(
                Id: 0,
                ctx.RunId,
                f.DedupeKey,
                c.Sha,
                c.Subject,
                ParseCommandThreadId(f.DedupeKey),
                Pushed: false,
                AiDrafted: f.Proposal.Origin == FixOrigin.LlmCommanded,
                ReplyPosted: false,
                CreatedAt: default)))
            .ToArray();
        await store.SavePushedFixesAsync(ctx.Pr, ctx.RunId, rows, ct).ConfigureAwait(false);

        try
        {
            await git.PushAsync(repoDir, pr.CloneUrl, branch, pr.SourceCommitSha, pat, ct).ConfigureAwait(false);
        }
        catch (PrHeadChangedException)
        {
            AutoFixTelemetry.AutoFixPushFailures.Add(
                1, new TagList {{ReviewForgeTelemetry.TagReason, "pin"}});
            await AbandonPushIntentAsync(ctx, ct).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // Policy/auth rejection: the run fails visibly; the committed work dies with the
            // private checkout and is never retried with force.
            AutoFixTelemetry.AutoFixPushFailures.Add(
                1, new TagList {{ReviewForgeTelemetry.TagReason, "rejected"}});
            logger.LogError(ex, "commit-fixes: push of {Count} commit(s) to {Branch} was rejected", committed.Count, branch);
            await AbandonPushIntentAsync(ctx, ct).ConfigureAwait(false);
            throw;
        }

        // Push confirmed on the remote: the intent rows become eligible for reply
        // reconciliation. The publish stage (this run, or a later one after a crash)
        // reconciles missing "Fixed in {sha}" replies from these rows alone.
        await store.ConfirmPushedFixesAsync(ctx.Pr, ctx.RunId, ct).ConfigureAwait(false);

        AutoFixTelemetry.AutoFixPushes.Add(1);
        logger.LogInformation(
            "commit-fixes: pushed {Count} commit(s) to {Branch} for run {RunId}",
            committed.Count, branch, ctx.RunId);

        // The run's own push is the one legal head movement; publish accepts this head.
        ctx.AutoFix = ctx.AutoFix with {PushedHeadSha = committed[^1].Sha};

        // (stage 8 queues no success replies in CommitOnHead mode); publish posts it.
        var commandReplies = committed
            .SelectMany(c => c.Fixes)
            .Where(f => f.Proposal.SourceThreadId is not null)
            .Select(f => (f.Proposal.SourceThreadId!.Value,
                CommentFormatter.CommittedFixLine(
                    f.CommitSha!, f.CommitSubject ?? string.Empty, f.Proposal.Origin == FixOrigin.LlmCommanded)))
            .ToArray();
        if (commandReplies.Length > 0)
        {
            ctx.AutoFix = ctx.AutoFix with
            {
                FixCommandReplies = [.. ctx.AutoFix.FixCommandReplies, .. commandReplies],
            };
        }
    }

    /// <summary>Best-effort cleanup of unconfirmed push-intent rows after a rejected push.
    /// Failure to clean up is logged but never masks the push failure that triggered it —
    /// stale intent rows are inert (never confirmed ⇒ never reconciled) and pruned with
    /// their run row.</summary>
    private async Task AbandonPushIntentAsync(ReviewContext ctx, CancellationToken ct)
    {
        try
        {
            await store.AbandonPushedFixesAsync(ctx.Pr, ctx.RunId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "commit-fixes: failed to abandon push-intent rows for run {RunId}", ctx.RunId);
        }
    }

    /// <summary>Commit grouping per AutoFix:CommitGranularity. "PerFix" = one group per FILE
    /// (staging is path-scoped; intra-file separation is impossible — same-file coalescing is
    /// the contract). "Single" = one group per run. Deterministic order (file path, ordinal).</summary>
    private IEnumerable<List<AppliedFix>> Group(List<AppliedFix> pending)
    {
        if (string.Equals(options.CommitGranularity, AutoFixOptions.GranularitySingle, StringComparison.Ordinal))
        {
            yield return [.. pending.OrderBy(f => f.Proposal.FilePath, RepoPath.PathComparer)];
            yield break;
        }

        foreach (var group in pending
                     .GroupBy(f => f.Proposal.FilePath, RepoPath.PathComparer)
                     .OrderBy(g => g.Key, RepoPath.PathComparer))
        {
            yield return [.. group];
        }
    }

    private static int? ParseCommandThreadId(string dedupeKey)
        => dedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal)
           && int.TryParse(dedupeKey[AppliedFix.CommandKeyPrefix.Length..], out var threadId)
            ? threadId
            : null;
}