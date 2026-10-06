using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

public sealed class ApplyFixesStage(NativeReviewAgent agent, int maxIterations) : IReviewStage
{
    public string Name => "apply-fixes";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var resolve = ctx.RequireResolveState();
        var applied = new List<AppliedResolution>();
        var outcomes = new Dictionary<int, ResolutionOutcome>(resolve.ResolutionOutcomes);
        var details = new Dictionary<int, string>(resolve.ResolutionDetails);
        var editors = new Dictionary<int, HashLineEditor>();
        foreach (var fix in resolve.ResolvePlan?.Fixes ?? [])
        {
            ct.ThrowIfCancellationRequested();
            PublishGuardChecks.ThrowIfClaimLost(ctx, $"before fix pass on thread {fix.ThreadId}");
            var collector = new ReviewCollector();
            var pass = await agent.RunWithEditToolsAsync(
                ResolvePromptBuilder.BuildFixPrompt([fix], ctx.WorkItems),
                collector,
                ctx.ContextStore,
                ctx.RequireRepoDir(),
                fix.CandidateFiles.ToHashSet(RepoPath.PathComparer),
                maxIterations,
                ct).ConfigureAwait(false);
            var files = fix.CandidateFiles.Where(path => pass.Editor.GetSessionChange(path) is not null).ToArray();
            var rationale = pass.Result.Narrative.ReviewSummary?.Trim() ?? string.Empty;
            var detail = rationale.Length == 0 ? "apply the requested correction" : rationale;
            if (!collector.Done && files.Length > 0)
            {
                foreach (var file in files) pass.Editor.RevertFile(file);
                files = [];
                detail = "the fix pass did not complete task_done";
            }
            if (files.Length == 0)
            {
                ResolveTelemetry.ResolveFixesDeclined.Add(fix.ThreadIds.Count);
                foreach (var threadId in fix.ThreadIds)
                {
                    outcomes[threadId] = ResolutionOutcome.AgentDeclined;
                    details[threadId] = detail;
                }
                continue;
            }
            foreach (var threadId in fix.ThreadIds)
            {
                applied.Add(new AppliedResolution(threadId, detail, files));
                outcomes[threadId] = ResolutionOutcome.Fixed;
                details[threadId] = detail;
                editors[threadId] = pass.Editor;
            }
            ResolveTelemetry.ResolveFixesApplied.Add(fix.ThreadIds.Count);
        }
        resolve.AppliedResolutions = applied;
        resolve.ResolutionOutcomes = outcomes;
        resolve.ResolutionDetails = details;
        resolve.ResolutionEditors = editors;
    }
}
