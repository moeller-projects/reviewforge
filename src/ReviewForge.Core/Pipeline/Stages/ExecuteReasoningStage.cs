using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;

namespace ReviewForge.Core.Pipeline.Stages;

/// <summary>
/// Stage 6: compose the active rulebook, build the prompt, and run the agent loop.
/// Findings stream to a per-run <c>findings/{runId}.jsonl</c> file so parallel workers
/// never interleave partial lines into a shared sink.
/// Large diffs optionally shard (map-reduce v1): one agent per shard runs concurrently on the
/// Fast/Full tier, each with its own collector (confinement + per-shard task_done); findings
/// merge into the single run collector with cross-shard dedupe. No merge pass: each shard's
/// prompt and read access cover its own files plus repo-wide context, but deep cross-file
/// semantic findings confined to no single shard are the accepted v1 loss.
/// </summary>
public sealed class ExecuteReasoningStage(
    NativeReviewAgent agent,
    string? findingsDir = null,
    int maxDiffChars = 200_000,
    int maxDiffCharsPerFile = 40_000,
    bool trivialDiffSkipEnabled = true,
    bool shardingEnabled = false,
    int shardMaxChars = 30_000,
    int maxShards = 8,
    int shardConcurrency = 2,
    ILogger<ExecuteReasoningStage>? logger = null) : IReviewStage
{
    // Defaults mirror PromptInput so unconfigured hosts keep the same prompt budget.

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
            ctx.PriorRun?.FindingKeys, findingsJsonl, ctx.RunId, ctx.PullRequest?.SourceCommitSha);

        // Status-aware dedupe (P1-11): known keys whose live thread is Fixed/Closed
        // resurface when regressed verbatim; Active/Pending threads keep suppressing.
        // Threads were fetched at stage 1 — no extra ADO call.
        ctx.ResolvedKeys = ctx.Threads
            .Where(t => t.DedupeKey is not null
                        && t.Status is ReviewThreadStatus.Fixed or ReviewThreadStatus.Closed)
            .Select(t => t.DedupeKey!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var finding in HomoglyphDiffAnalyzer.Analyze(ctx.DiffText))
        {
            var key = DedupeKey.Compute(
                finding.RuleId,
                finding.Anchor?.FilePath ?? "-",
                finding.Snippet);
            if (ctx.Collector.IsKnown(key))
            {
                if (ctx.Collector.WasKnownAtStart(key))
                {
                    if (ctx.ResolvedKeys.Contains(key) && !ctx.Collector.RegressedKeys.Contains(key))
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
        if (trivialDiffSkipEnabled && ctx.ReviewableFiles is { } reviewable
            && TrivialDiff.IsTrivial(ctx.Diff ?? DiffIndex.Parse(ctx.DiffText), ctx.PendingReplies, reviewable))
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

        ShardPlan? sharded = null;
        if (shardingEnabled)
        {
            var candidate = ShardPlanner.Plan(ctx.DiffText, shardMaxChars, maxShards);
            if (candidate.Overflowed)
            {
                // Cap overflow falls back to the legacy truncated single-agent path — never a run failure.
                ReviewTelemetry.ShardFallback.Add(1);
                logger?.LogWarning(
                    "diff of {DiffChars} chars overflows {MaxShards} shards of {ShardMaxChars} chars; falling back to the single-agent path",
                    ctx.DiffText.Length, maxShards, shardMaxChars);
            }
            else if (candidate.Shards.Count >= 2)
            {
                sharded = candidate;
            }
        }

        var rootFiles = Directory.Exists(repoDir) ? Directory.GetFiles(repoDir, "*", SearchOption.TopDirectoryOnly) : [];
        if (sharded is { } plan)
        {
            await RunShardsAsync(ctx, repoDir, rootFiles, plan, ct).ConfigureAwait(false);
            return;
        }

        var ruleBook = agent.ComposeRuleBook(ctx.ChangedFiles, rootFiles);
        var prompt = PromptBuilder.Build(new PromptInput(
            Pr: ctx.RequirePullRequest(), Kind: ctx.Kind, WorkItems: ctx.WorkItems, ChangedFiles: ctx.ChangedFiles,
            PendingReplies: ctx.PendingReplies, DiffText: ctx.DiffText, Enrichment: null, ContextNames: ctx.ContextStore.Names,
            MaxDiffChars: maxDiffChars, MaxDiffCharsPerFile: maxDiffCharsPerFile));
        ctx.Result = await agent.RunAsync(new AgentRunRequest(
            prompt, ctx.Collector, ctx.ContextStore, repoDir, ToolProfile.Review,
            ruleBook, ctx.ChangedFiles.ToHashSet(RepoPath.PathComparer), ctx.Diff, ctx.DiffText,
            ctx.ResolvedKeys, Tier: ctx.Kind == ReviewKind.FollowUp ? ChatTier.Fast : ChatTier.Full), ct);
    }

    private async Task RunShardsAsync(
        ReviewContext ctx, string repoDir, string[] rootFiles, ShardPlan plan, CancellationToken ct)
    {
        var tier = ctx.Kind == ReviewKind.FollowUp ? ChatTier.Fast : ChatTier.Full;
        var shards = plan.Shards;
        var shardCollectors = new ReviewCollector[shards.Count];
        var shardNarratives = new ReviewNarrative?[shards.Count];

        // One shard failing fails the run (no engine fallback): Parallel.ForEachAsync cancels
        // the remaining shards and rethrows, and ctx.Result is never set so nothing publishes.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, shards.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, shardConcurrency),
                CancellationToken = ct,
            },
            async (i, token) =>
            {
                var shardStart = Stopwatch.GetTimestamp();
                var shard = shards[i];

                // Per-shard collector: confines RecordFinding's changed-files guard to the
                // shard and gives each shard agent its own task_done signal. Merged below.
                var shardCollector = new ReviewCollector(ctx.PriorRun?.FindingKeys);
                shardCollectors[i] = shardCollector;

                var shardPrompt = PromptBuilder.Build(new PromptInput(
                    Pr: ctx.RequirePullRequest(), Kind: ctx.Kind, WorkItems: ctx.WorkItems,
                    ChangedFiles: shard.Files, PendingReplies: ctx.PendingReplies, DiffText: shard.DiffText,
                    Enrichment: null, ContextNames: ctx.ContextStore.Names,
                    MaxDiffChars: Math.Max(shard.DiffText.Length, 1), MaxDiffCharsPerFile: maxDiffCharsPerFile));
                var result = await agent.RunAsync(new AgentRunRequest(
                    shardPrompt, shardCollector, SnapshotContext(ctx.ContextStore), repoDir, ToolProfile.Review,
                    agent.ComposeRuleBook(shard.Files, rootFiles),
                    shard.Files.ToHashSet(RepoPath.PathComparer), DiffIndex.Parse(shard.DiffText), shard.DiffText,
                    ctx.ResolvedKeys, Tier: tier), token);
                shardNarratives[i] = result.Narrative;
                ReviewTelemetry.ShardDurationMilliseconds.Record(
                    Stopwatch.GetElapsedTime(shardStart).TotalMilliseconds,
                    new KeyValuePair<string, object?>(ReviewForgeTelemetry.TagShards, shards.Count));
            }).ConfigureAwait(false);

        var allDone = true;
        for (var i = 0; i < shardCollectors.Length; i++)
        {
            ctx.Collector.MergeFrom(shardCollectors[i]);
            allDone &= shardCollectors[i].Done;
        }

        ctx.Result = new ReviewResult
        {
            Narrative = MergeNarratives(shardNarratives),
            Findings = ctx.Collector.Findings,
            Uncertainties = ctx.Collector.Uncertainties,
            ReviewDepth = allDone
                ? $"agentic tool loop ({shards.Count} shards)"
                : $"iteration cap reached — task_done missing in one or more of {shards.Count} shards",
            RuleBookVersion = agent.ComposeRuleBook(ctx.ChangedFiles, rootFiles).VersionHash,
        };
    }

    /// <summary>ContextStore is read-only for the agent but not thread-safe; each shard gets a
    /// point-in-time snapshot instead of sharing the run store.</summary>
    private static ContextStore SnapshotContext(ContextStore source)
    {
        var snapshot = new ContextStore();
        foreach (var name in source.Names)
        {
            snapshot.Put(name, source.Read(name)!);
        }

        return snapshot;
    }

    private static ReviewNarrative MergeNarratives(IReadOnlyList<ReviewNarrative?> narratives)
    {
        static string? Join(IEnumerable<string?> parts)
        {
            var nonEmpty = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToArray();
            return nonEmpty.Length > 0 ? string.Join("\n\n---\n\n", nonEmpty) : null;
        }

        return new ReviewNarrative
        {
            ReviewSummary = Join(narratives.Select(n => n?.ReviewSummary)),
            VerificationSummary = Join(narratives.Select(n => n?.VerificationSummary)),
            PrSummary = Join(narratives.Select(n => n?.PrSummary)),
            GoodPractices = [.. narratives.SelectMany(n => n?.GoodPractices ?? [])],
            AcceptanceCriteria = [.. narratives.SelectMany(n => n?.AcceptanceCriteria ?? [])],
            // Every shard sees the full PendingReplies list, so duplicate actions for the
            // same thread are expected: first shard wins (matches ThreadTriage's
            // FirstOrDefault), the rest are dropped deterministically.
            ThreadActions = [.. narratives
                .SelectMany(n => n?.ThreadActions ?? [])
                .GroupBy(a => a.ThreadId)
                .Select(g => g.First())],
        };
    }
}