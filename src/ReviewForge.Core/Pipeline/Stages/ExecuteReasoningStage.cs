using ReviewForge.Core.Analysis;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 6: compose the active rulebook, build the prompt, and run the agent loop.
/// Findings stream to a per-run <c>findings/{runId}.jsonl</c> file so parallel workers
/// never interleave partial lines into a shared sink.
/// </summary>
public sealed class ExecuteReasoningStage(
    NativeReviewAgent agent,
    string? findingsDir = null,
    int maxDiffChars = 200_000,
    int maxDiffCharsPerFile = 40_000) : IReviewStage
{
    // Defaults mirror PromptInput so unconfigured hosts keep the same prompt budget.

    public string Name => "execute-reasoning";

    public int Order => 60;

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var repoDir = ctx.RequireRepoDir();
        // Direct System.IO by design — see IWorkspaceFs scope note. Findings JSONL is a
        // single-writer per-run artifact; root-file enumeration feeds rule activation only.
        using var findingsJsonl = findingsDir is null
            ? null
            : new StreamWriter(Path.Combine(findingsDir, $"{ctx.RunId:N}.jsonl"), append: true) {AutoFlush = true};
        ctx.Collector = new ReviewCollector(
            ctx.PriorRun?.FindingKeys, findingsJsonl, ctx.RunId, ctx.PullRequest?.SourceCommitSha);
        foreach (var finding in HomoglyphDiffAnalyzer.Analyze(ctx.DiffText))
            ctx.Collector.AddFinding(finding);
        var rootFiles = Directory.Exists(repoDir) ? Directory.GetFiles(repoDir, "*", SearchOption.TopDirectoryOnly) : [];
        var prompt = PromptBuilder.Build(new PromptInput(
            Pr: ctx.RequirePullRequest(), Kind: ctx.Kind, WorkItems: ctx.WorkItems, ChangedFiles: ctx.ChangedFiles,
            PendingReplies: ctx.PendingReplies, DiffText: ctx.DiffText, Enrichment: null, ContextNames: ctx.ContextStore.Names,
            MaxDiffChars: maxDiffChars, MaxDiffCharsPerFile: maxDiffCharsPerFile));
        ctx.Result = await agent.RunAsync(
            prompt,
            ctx.Collector,
            ctx.ContextStore,
            repoDir,
            ruleBook,
            ctx.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ctx.Diff,
            ct);
    }
}