using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
namespace ReviewForge.Service;

/// <summary>Service options for the pipeline host.</summary>
public sealed class ReviewForgeServiceOptions
{
    public const string SectionName = "ReviewForge";

    /// <summary>Root for repository checkouts (one subdir per repository).</summary>
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "reviewforge");

    /// <summary>SQLite connection string for the finding store.</summary>
    public string StoreConnectionString { get; init; } = "Data Source=reviewforge.db";

    /// <summary>Optional path to a system-prompt override file.</summary>
    public string? PromptOverridePath { get; init; }

    /// <summary>Optional directory containing rule-pack JSON overrides.</summary>
    public string? RuleSetsPath { get; init; }

    public int MaxContextTokens { get; init; } = 150_000;
    public int MaxIterations { get; init; } = 30;
    public int WorkerCount { get; init; } = 1;
    public CheckoutEvictionOptions Checkout { get; init; } = new();
    public ReasoningEffort? ReasoningEffort { get; init; }
}

public sealed class ReviewPipelineFactory(
    IPullRequestSource source,
    IFindingStore store,
    RepoCheckoutPool checkoutPool,
    IChatClientFactory chatClientFactory,
    IOptions<ReviewForgeServiceOptions> options,
    ILoggerFactory loggerFactory,
    IContextEnricher? enricher = null,
    TimeProvider? clock = null)
{
    public ReviewPipeline Create()
    {
        var opts = options.Value;
        var agent = new NativeReviewAgent(chatClientFactory, new AgentOptions
        {
            MaxContextTokens = opts.MaxContextTokens,
            MaxIterations = opts.MaxIterations,
            PromptOverridePath = opts.PromptOverridePath,
            RuleSetsPath = opts.RuleSetsPath,
            Effort = opts.ReasoningEffort,
        }, loggerFactory.CreateLogger<NativeReviewAgent>());
        Directory.CreateDirectory(opts.WorkDir);
        var findingsDir = Path.Combine(opts.WorkDir, "findings");
        Directory.CreateDirectory(findingsDir);

        IReviewStage[] stages =
        [
            new FetchPrContextStage(source, store),
            new ReviewGateStage(clock),
            new PrepareRepositoryStage(checkoutPool),
            new ClassifyRunStage(source),
            new EnrichContextStage(enricher, loggerFactory.CreateLogger<EnrichContextStage>()),
            new ExecuteReasoningStage(agent, findingsDir),
            new ValidateFindingsStage(loggerFactory.CreateLogger<ValidateFindingsStage>()),
            new TriageThreadsStage(source, loggerFactory.CreateLogger<TriageThreadsStage>()),
            new PublishFindingsStage(source, loggerFactory.CreateLogger<PublishFindingsStage>()),
            new PersistRunStage(store, clock),
        ];

        return new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>());
    }
}