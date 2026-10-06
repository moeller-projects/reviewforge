using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 6: compose the active rulebook, build the prompt, and run one bounded-context agent loop.
/// Findings stream to a per-run <c>findings/{runId}.jsonl</c> file so parallel workers
/// never interleave partial lines into a shared sink.
/// </summary>
public sealed class ExecuteReasoningStage(
    NativeReviewAgent agent,
    int maxDiffChars,
    int maxDiffCharsPerFile,
    string? findingsDir = null,
    bool trivialDiffSkipEnabled = true) : IReviewStage
{

    public string Name => "execute-reasoning";

    public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
    {
        var repoDir = ctx.RequireRepoDir();
        // Direct System.IO by design — see IWorkspaceFs scope note. Findings JSONL is a
        // single-writer per-run artifact; root-file enumeration feeds rule activation only.
        using var findingsJsonl = findingsDir is null
            ? null
            : new StreamWriter(Path.Combine(findingsDir, $"{ctx.RunId:N}.jsonl"), append: true) {AutoFlush = true};
        ctx.Collector = new ReviewCollector(
            ctx.Fetch.PriorRun?.FindingKeys, findingsJsonl, ctx.RunId, ctx.Fetch.PullRequest?.SourceCommitSha);

        // Status-aware dedupe (P1-11): known keys whose live thread is Fixed/Closed
        // resurface when regressed verbatim; Active/Pending threads keep suppressing.
        // Threads were fetched at stage 1 — no extra ADO call.
        ctx.Fetch = ctx.Fetch with
        {
            ResolvedKeys = ctx.Fetch.Threads
                .Where(t => t.DedupeKey is not null
                            && t.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
                .Select(t => t.DedupeKey!)
                .ToHashSet(StringComparer.Ordinal),
        };

        foreach (var finding in HomoglyphDiffAnalyzer.Analyze(ctx.Repository.DiffText))
        {
            var key = DedupeKey.Compute(
                finding.RuleId,
                finding.Anchor?.FilePath ?? "-",
                finding.Snippet);
            if (ctx.Collector.IsKnown(key))
            {
                if (ctx.Collector.WasKnownAtStart(key))
                {
                    if (ctx.Fetch.ResolvedKeys.Contains(key) && !ctx.Collector.RegressedKeys.Contains(key))
                    {
                        finding.DedupeKey = key;
                        finding.IsRegression = true;
                        ctx.Collector.AddFinding(finding);
                        ctx.Collector.MarkRegressed(key);
                    }
                    else
                    {
                        ctx.Collector.MarkRedetected(key);
                    }
                }

                continue;
            }

            finding.DedupeKey = key;
            ctx.Collector.AddFinding(finding);
        }

        // Trivial-diff fast path: zero added reviewable lines and nothing awaiting an
        // answer → clean vote without an LLM call. The deterministic homoglyph analyzer
        // above has already run, so its findings are still recorded. Unknown reviewability
        // (stage-3 fields unset, e.g. stage unit tests) never skips.
        if (trivialDiffSkipEnabled && ctx.Repository.ReviewableFiles is { } reviewable
            && TrivialDiff.IsTrivial(ctx.Repository.Diff ?? DiffIndex.Parse(ctx.Repository.DiffText), ctx.PendingReplies, reviewable))
        {
            ReviewTelemetry.TrivialReviews.Add(1);
            // The synthetic result must carry the collector's findings: the homoglyph
            // analyzer ran above and downstream stages (validate/publish/summary/vote)
            // consume ctx.Result, not the collector.
            ctx.Result = new ReviewResult
            {
                Narrative = new ReviewNarrative {ReviewSummary = "No reviewable changes in this iteration."},
                Findings = ctx.Collector.Findings,
                Uncertainties = ctx.Collector.Uncertainties,
                ReviewDepth = "trivial diff — no agent run",
            };
            return;
        }

        var rootFiles = Directory.Exists(repoDir) ? Directory.GetFiles(repoDir, "*", SearchOption.TopDirectoryOnly) : [];

        var ruleBook = agent.ComposeRuleBook(ctx.Fetch.ChangedFiles, rootFiles);
        var prompt = PromptBuilder.Build(new PromptInput(
            Pr: ctx.RequirePullRequest(), Kind: ctx.Kind, WorkItems: ctx.Fetch.WorkItems, ChangedFiles: ctx.Fetch.ChangedFiles,
            PendingReplies: ctx.PendingReplies, DiffText: ctx.Repository.DiffText, Enrichment: null, ContextNames: ctx.ContextStore.Names,
            MaxDiffChars: maxDiffChars, MaxDiffCharsPerFile: maxDiffCharsPerFile));
        ctx.Result = await agent.RunAsync(new AgentRunRequest(
            prompt, ctx.Collector, ctx.ContextStore, repoDir, ToolProfile.Review,
            ruleBook, ctx.Fetch.ChangedFiles.ToHashSet(RepoPath.PathComparer), ctx.Repository.Diff, ctx.Repository.DiffText,
            ctx.Fetch.ResolvedKeys, Tier: ctx.Kind == ReviewKind.FollowUp ? ChatTier.Fast : ChatTier.Full), ct);
    }

}