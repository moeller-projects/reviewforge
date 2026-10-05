using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

public sealed class ReviewPipelineFactory(
    IPullRequestSource source,
    IFindingStore store,
    RepoCheckoutPool checkoutPool,
    IChatClientFactory chatClientFactory,
    IOptions<ReviewOptions> options,
    IOptions<WorkspaceOptions> workspaceOptions,
    IOptions<RepoReadToolsOptions> repoReadToolsOptions,
    ILoggerFactory loggerFactory,
    IContextEnricher? enricher = null,
    TimeProvider? clock = null,
    IEnumerable<IFindingFixer>? findingFixers = null,
    IOptions<AutoFixOptions>? autoFixOptions = null,
    IOptions<VerifyFindingsOptions>? verifyFindingsOptions = null,
    IGitOps? gitOps = null,
    string? pushPat = null)
{
    public ReviewPipeline Create()
    {
        var opts = options.Value;
        var repoReadToolsOpts = repoReadToolsOptions.Value;
        var cleanVote = opts.CleanRunVote.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? (ReviewerVote?) null
            : Enum.TryParse<ReviewerVote>(opts.CleanRunVote, ignoreCase: true, out var parsedVote)
              && parsedVote is ReviewerVote.NoResponse or ReviewerVote.Approved or ReviewerVote.ApprovedWithSuggestions
                ? parsedVote
                : throw new InvalidOperationException(
                    $"Review:CleanRunVote '{opts.CleanRunVote}' is invalid; expected NoResponse | Approved | ApprovedWithSuggestions | None");
        var agent = new NativeReviewAgent(chatClientFactory, new AgentOptions
        {
            MaxContextTokens = opts.MaxContextTokens,
            MaxIterations = opts.MaxIterations,
            PromptOverridePath = opts.PromptOverridePath,
            RuleSetsPath = opts.RuleSetsPath,
            Effort = opts.ReasoningEffort,
            DebugLogging = opts.AgentDebugLogging,
            GrepMaxMs = repoReadToolsOpts.GrepMaxMs,
            GrepMaxLines = repoReadToolsOpts.GrepMaxLines,
        }, loggerFactory.CreateLogger<NativeReviewAgent>());

        var fixers = findingFixers ?? [];
        var registry = new FindingFixerRegistry(fixers);
        var autoFix = autoFixOptions?.Value ?? new AutoFixOptions();
        var factoryLogger = loggerFactory.CreateLogger<ReviewPipelineFactory>();
        foreach (var ruleId in autoFix.AllowedRuleIds)
        {
            if (!registry.TryGet(ruleId, out _))
            {
                factoryLogger.LogWarning(
                    "AutoFix:AllowedRuleIds entry '{RuleId}' has no registered fixer — it can never match", ruleId);
            }
        }

        var findingsDir = Path.Combine(workspaceOptions.Value.WorkDir, "findings");

        var diffBudget = new DiffBudget(
            opts.MaxDiffBytes,
            opts.MaxDiffBytesPerFile,
            opts.DiffExcludeGlobs ?? DiffBudget.Default.ExcludeGlobs);

        var stages = new List<IReviewStage>
        {
            new FetchPrContextStage(source, store),
            new ReviewGateStage(clock),
            new PrepareRepositoryStage(
                checkoutPool,
                loggerFactory.CreateLogger<PrepareRepositoryStage>(),
                diffBudget,
                source,
                enricher,
                clock,
                // Private run-scoped checkout iff CommitOnHead is active; with Enabled=false
                // the pipeline stays byte-identical — pooled checkout included.
                checkoutMode: autoFix.IsCommitOnHead ? CheckoutMode.Private : CheckoutMode.Pooled,
                autoFix: autoFix),
            new ClassifyRunStage(source),
            new EnrichContextStage(enricher, loggerFactory.CreateLogger<EnrichContextStage>()),
            new ExecuteReasoningStage(
                agent, findingsDir,
                maxDiffChars: opts.MaxDiffChars, maxDiffCharsPerFile: opts.MaxDiffCharsPerFile,
                trivialDiffSkipEnabled: opts.TrivialDiffSkipEnabled,
                shardingEnabled: opts.Sharding.Enabled,
                shardMaxChars: opts.Sharding.ShardMaxChars,
                maxShards: opts.Sharding.MaxShards,
                shardConcurrency: opts.Sharding.ShardConcurrency),
            new ValidateFindingsStage(loggerFactory.CreateLogger<ValidateFindingsStage>()),
        };

        var verifyOptions = verifyFindingsOptions?.Value;
        if (verifyOptions?.Enabled == true)
        {
            stages.Add(new VerifyFindingsStage(
                chatClientFactory,
                verifyOptions,
                loggerFactory.CreateLogger<VerifyFindingsStage>()));
        }

        stages.AddRange(
        [
            new AutoFixFindingsStage(
                registry, agent, autoFix, loggerFactory.CreateLogger<AutoFixFindingsStage>(),
                store: store),
            new BeginRunStage(store, clock),
        ]);

        // Stage 7.7: registered whenever the git write surface is available; it no-ops unless
        // CommitOnHead is active. CommitOnHead without IGitOps is a composition error — fail fast.
        if (gitOps is not null)
        {
            stages.Add(new CommitFixesStage(
                gitOps, store, autoFix, loggerFactory.CreateLogger<CommitFixesStage>(), pushPat));
        }
        else if (autoFix.IsCommitOnHead)
        {
            throw new InvalidOperationException("IGitOps is required when AutoFix:PublishMode is CommitOnHead");
        }

        stages.AddRange(
        [
            new TriageThreadsStage(source, loggerFactory.CreateLogger<TriageThreadsStage>()),
            new PublishFindingsStage(
                source, store, loggerFactory.CreateLogger<PublishFindingsStage>(), cleanVote, autoFix),
            new PersistRunStage(store, clock),
        ]);

        return new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>());
    }
}