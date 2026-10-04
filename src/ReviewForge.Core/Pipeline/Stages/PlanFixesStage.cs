using System.Diagnostics;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class PlanFixesStage(int maxThreads, int maxWritableFiles) : IReviewStage
{
    public string Name => "plan-fixes";
    public int Order => 55;

    public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var comments = ctx.ResolvableComments.ToDictionary(c => c.ThreadId);
        var changed = ctx.ChangedFiles.Select(RepoPath.Normalize).ToHashSet(RepoPath.PathComparer);
        var verdicts = new List<ThreadVerdict>(ctx.ThreadVerdicts.Count);
        var outcomes = new Dictionary<int, ResolutionOutcome>();
        var details = new Dictionary<int, string>();
        foreach (var verdict in ctx.ThreadVerdicts)
        {
            if (!comments.TryGetValue(verdict.ThreadId, out var comment)) continue;
            if (verdict.Verdict != TriageVerdict.Actionable)
            {
                outcomes[verdict.ThreadId] = verdict.Verdict switch
                {
                    TriageVerdict.NonIssue => ResolutionOutcome.NonIssue,
                    TriageVerdict.Question => ResolutionOutcome.Question,
                    TriageVerdict.AlreadyFixed => ResolutionOutcome.AlreadyFixed,
                    _ => ResolutionOutcome.OutOfScope,
                };
                verdicts.Add(verdict);
                continue;
            }
            if (!comment.CommenterAllowed || comment.Anchor is null || !changed.Contains(RepoPath.Normalize(comment.Anchor.FilePath)))
            {
                details[verdict.ThreadId] = !comment.CommenterAllowed
                    ? "commenter is not authorized to request edits"
                    : comment.Anchor is null
                        ? "comment has no safe file anchor"
                        : "anchored file is not in the pull-request changed-file manifest";
                outcomes[verdict.ThreadId] = ResolutionOutcome.OutOfScope;
                verdicts.Add(verdict with { Verdict = TriageVerdict.OutOfScope });
                continue;
            }
            verdicts.Add(verdict);
        }
        var plan = ResolvePlanner.Plan(ctx.ResolvableComments, verdicts, changed.ToArray(), maxThreads, maxWritableFiles);
        foreach (var (threadId, reason) in plan.Deferred)
        {
            outcomes[threadId] = ResolutionOutcome.Deferred;
            details[threadId] = reason;
            ReviewForgeTelemetry.ResolveDeferred.Add(1, new TagList { { "reason", reason } });
        }
        ctx.ThreadVerdicts = verdicts;
        ctx.ResolvePlan = plan;
        ctx.ResolutionOutcomes = outcomes;
        ctx.ResolutionDetails = details;
        return Task.CompletedTask;
    }
}
