using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class ResolveCommitPushStage(
    IGitOps git,
    IFindingStore store,
    string commitGranularity,
    string authorName,
    string authorEmail,
    string? pat = null,
    ILogger<ResolveCommitPushStage>? logger = null,
    TimeProvider? clock = null) : IReviewStage
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    public string Name => "resolve-commit-push";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var applied = ctx.AppliedResolutions.ToArray();
        if (applied.Length > 0)
        {
            var pr = ctx.RequirePullRequest();
            if (pr.SourceRefName is not { } refName || !refName.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                ctx.ResolutionOutcomes = ctx.ResolutionOutcomes.ToDictionary(
                    item => item.Key,
                    item => item.Value == ResolutionOutcome.Fixed ? ResolutionOutcome.OutOfScope : item.Value);
                var details = ctx.ResolutionDetails.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var item in applied)
                    details[item.ThreadId] = "The fix could not be committed because this pull request has no source branch reference.";
                ctx.ResolutionDetails = details;
                ctx.AppliedResolutions = [];
                logger?.LogWarning("resolve push unavailable: source ref is missing or not a branch for {Pr}", ctx.Pr);
            }
            else
            {
                await CommitAndPushAsync(ctx, applied, pr, refName["refs/heads/".Length..], ct).ConfigureAwait(false);
            }
        }
        await PersistActionsAsync(ctx, ct).ConfigureAwait(false);
    }

    private async Task CommitAndPushAsync(
        ReviewContext ctx, AppliedResolution[] applied, PullRequest pr, string branch, CancellationToken ct)
    {
        PublishGuardChecks.ThrowIfClaimLost(ctx, "before resolve commit");
        var single = string.Equals(commitGranularity, "Single", StringComparison.OrdinalIgnoreCase);
        var groups = single
            ? [applied]
            : applied.GroupBy(r => string.Join("\0", r.Files.Order(RepoPath.PathComparer)), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        try
        {
            foreach (var group in groups)
            {
                var representative = group[0];
                var verdict = ctx.ThreadVerdicts.FirstOrDefault(v => v.ThreadId == representative.ThreadId);
                var threadIds = group.Select(r => r.ThreadId).Order().ToArray();
                var message = ConventionalCommitBuilder.BuildResolve(
                    ctx.RunId, ctx.Pr.PrId, representative.ThreadId, verdict?.Category ?? "bug",
                    representative.Files[0], representative.Rationale, verdict?.Evidence ?? string.Empty, threadIds);
                var paths = single ? group.SelectMany(r => r.Files).Distinct(RepoPath.PathComparer).ToArray() : representative.Files;
                var sha = await git.CommitAsync(ctx.RequireRepoDir(), message, authorName, authorEmail, paths, ct).ConfigureAwait(false);
                var subject = ConventionalCommitBuilder.SubjectOf(message);
                for (var i = 0; i < applied.Length; i++)
                {
                    if (threadIds.Contains(applied[i].ThreadId))
                        applied[i] = applied[i] with { CommitSha = sha, CommitSubject = subject };
                }
            }

            var tip = await git.GetRemoteTipAsync(ctx.RequireRepoDir(), pr.CloneUrl, branch, pat, ct).ConfigureAwait(false);
            if (!string.Equals(tip, pr.SourceCommitSha, StringComparison.OrdinalIgnoreCase))
                throw new PrHeadChangedException(pr.SourceCommitSha, tip ?? "<missing>");
            // The claim can be lost during the commit loop and the asynchronous tip read
            // above; re-check it at the irreversible boundary itself, directly before push.
            PublishGuardChecks.ThrowIfClaimLost(ctx, "at resolve push");
            await git.PushAsync(ctx.RequireRepoDir(), pr.CloneUrl, branch, pr.SourceCommitSha, pat, ct).ConfigureAwait(false);
        }
        catch (PrHeadChangedException)
        {
            ResolveTelemetry.ResolvePushFailures.Add(1, new TagList { { "reason", "pin" } });
            await PersistPushFailureAsync(ctx, applied, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            ResolveTelemetry.ResolvePushFailures.Add(1, new TagList { { "reason", "rejected" } });
            await PersistPushFailureAsync(ctx, applied, ct).ConfigureAwait(false);
            throw;
        }
        ResolveTelemetry.ResolveCommitsPushed.Add(groups.Length);
        ctx.AppliedResolutions = applied;
    }

    private async Task PersistPushFailureAsync(ReviewContext ctx, AppliedResolution[] applied, CancellationToken ct)
    {
        var outcomes = new Dictionary<int, ResolutionOutcome>(ctx.ResolutionOutcomes);
        var details = new Dictionary<int, string>(ctx.ResolutionDetails);
        foreach (var item in applied)
        {
            outcomes[item.ThreadId] = ResolutionOutcome.PushFailed;
            details[item.ThreadId] = "the resolution was not published because local commit creation or branch push failed";
        }
        ctx.ResolutionOutcomes = outcomes;
        ctx.ResolutionDetails = details;
        ctx.AppliedResolutions = applied;
        await PersistActionsAsync(ctx, ct).ConfigureAwait(false);
    }

    private async Task PersistActionsAsync(ReviewContext ctx, CancellationToken ct)
    {
        var verdicts = ctx.ThreadVerdicts.ToDictionary(v => v.ThreadId);
        var resolutions = ctx.AppliedResolutions.ToDictionary(r => r.ThreadId);
        var actions = ctx.ResolvableComments.Select(comment =>
        {
            var verdict = verdicts.GetValueOrDefault(comment.ThreadId)
                          ?? new ThreadVerdict(comment.ThreadId, TriageVerdict.OutOfScope, "no verdict", "low");
            var outcome = ctx.ResolutionOutcomes.GetValueOrDefault(comment.ThreadId, ResolutionOutcome.OutOfScope);
            var commit = resolutions.GetValueOrDefault(comment.ThreadId)?.CommitSha;
            if (outcome == ResolutionOutcome.Fixed && commit is null) outcome = ResolutionOutcome.OutOfScope;
            return new ResolveAction(0, ctx.RunId, comment.ThreadId, verdict.Verdict, outcome, commit, false, _Clock.GetUtcNow());
        }).ToArray();
        await store.SaveResolveActionsAsync(ctx.Pr, ctx.RunId, actions, ct).ConfigureAwait(false);
        ctx.ResolveActions = await store.GetResolveActionsAsync(ctx.Pr, actions.Select(a => a.ThreadId).ToArray(), ct).ConfigureAwait(false);
    }
}
