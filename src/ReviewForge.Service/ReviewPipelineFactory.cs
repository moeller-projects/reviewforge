using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
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
    public int WorkerCount { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Dedicated threads for LibGit2Sharp work (clones/fetches/diffs).</summary>
    public int GitMaxConcurrency { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    /// <summary>
    /// Reviewer vote set on clean runs (no findings, all acceptance criteria met, no
    /// unanswered threads): NoResponse (default) | Approved | ApprovedWithSuggestions |
    /// None (leave the vote untouched).
    /// </summary>
    public string CleanRunVote { get; init; } = "NoResponse";
    public bool TargetedFetchEnabled { get; init; }
    public CheckoutEvictionOptions Checkout { get; init; } = new();
    public ReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>Enables argument-length debug breadcrumbs in the agent loop (category
    /// level Debug is still required). Default false.</summary>
    public bool AgentDebugLogging { get; init; }

    /// <summary>Total diff budget for the review prompt (~50k tokens); oversized diffs are truncated with a marker.</summary>
    public int MaxDiffChars { get; init; } = 200_000;

    /// <summary>Per-file diff budget; files over it keep their header plus a bounded prefix.</summary>
    public int MaxDiffCharsPerFile { get; init; } = 40_000;
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
        var cleanVote = opts.CleanRunVote switch
        {
            "None" => (ReviewerVote?)null,
            var s when Enum.TryParse<ReviewerVote>(s, ignoreCase: true, out var v)
                         && v is ReviewerVote.NoResponse or ReviewerVote.Approved or ReviewerVote.ApprovedWithSuggestions => v,
            var s => throw new InvalidOperationException(
                $"ReviewForge:CleanRunVote '{s}' is invalid; expected NoResponse | Approved | ApprovedWithSuggestions | None"),
        };
        var agent = new NativeReviewAgent(chatClientFactory, new AgentOptions
        {
            MaxContextTokens = opts.MaxContextTokens,
            MaxIterations = opts.MaxIterations,
            PromptOverridePath = opts.PromptOverridePath,
            RuleSetsPath = opts.RuleSetsPath,
            Effort = opts.ReasoningEffort,
            DebugLogging = opts.AgentDebugLogging,
        }, loggerFactory.CreateLogger<NativeReviewAgent>());
        Directory.CreateDirectory(opts.WorkDir);
        var findingsDir = Path.Combine(opts.WorkDir, "findings");
        Directory.CreateDirectory(findingsDir);

        IReviewStage[] stages =
        [
            new FetchPrContextStage(source, store),
            new ReviewGateStage(clock),
            new PrepareRepositoryStage(checkoutPool, loggerFactory.CreateLogger<PrepareRepositoryStage>()),
            new ClassifyRunStage(source),
            new EnrichContextStage(enricher, loggerFactory.CreateLogger<EnrichContextStage>()),
            new ExecuteReasoningStage(agent, findingsDir, maxDiffChars: opts.MaxDiffChars, maxDiffCharsPerFile: opts.MaxDiffCharsPerFile),
            new ValidateFindingsStage(loggerFactory.CreateLogger<ValidateFindingsStage>()),
            new BeginRunStage(store, clock),
            new TriageThreadsStage(source, loggerFactory.CreateLogger<TriageThreadsStage>()),
            new PublishFindingsStage(source, store, loggerFactory.CreateLogger<PublishFindingsStage>(), cleanVote),
            new PersistRunStage(store, clock),
        ];

        return new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>());
    }
}