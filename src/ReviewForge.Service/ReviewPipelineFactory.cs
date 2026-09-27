using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

/// <summary>Map-reduce sharding for large diffs. When enabled and the planned shard count is
/// at least two, stage 6 runs one agent per shard concurrently and merges findings into the
/// single run collector. Shard-cap overflow never fails a run — it falls back to the legacy
/// truncated single-agent path (counted by reviewforge.shard.fallback_total).</summary>
public sealed class ShardingOptions
{
    public bool Enabled { get; init; }

    /// <summary>Cumulative diff chars per shard; a file larger than this gets a shard of its own.</summary>
    public int ShardMaxChars { get; init; } = 30_000;

    /// <summary>Shard cap. Overflow → legacy fallback, never a run failure.</summary>
    public int MaxShards { get; init; } = 8;

    /// <summary>Max concurrent shard agents (bounded by the LLM governor as well).</summary>
    public int ShardConcurrency { get; init; } = 2;
}

/// <summary>Service options for the pipeline host.</summary>
public sealed class ReviewForgeServiceOptions
{
    public const string SectionName = "ReviewForge";

    /// <summary>Root for repository checkouts (one subdir per repository).</summary>
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "reviewforge");

    /// <summary>SQLite connection string for the finding store.</summary>
    public string StoreConnectionString { get; init; } = "Data Source=reviewforge.db";

    /// <summary>Ingest queue backing: "Memory" (default, in-memory channel — queued runs are
    /// lost on restart) or "Sqlite" (durable rows on the store's database file).</summary>
    public string QueueMode { get; init; } = nameof(Service.Queue.QueueMode.Memory);

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
    public string CleanRunVote { get; init; } = "NoResponse";

    internal static bool IsValidCleanRunVote(string? value)
        => value is not null
           && (value.Equals("None", StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.NoResponse), StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.Approved), StringComparison.OrdinalIgnoreCase)
               || value.Equals(nameof(ReviewerVote.ApprovedWithSuggestions), StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Extra diff exclusion globs; replace the default set when set (array replace, not merge).</summary>
    public string[]? DiffExcludeGlobs { get; init; }
    public long MaxDiffBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxDiffBytesPerFile { get; init; } = 256 * 1024;

    /// <summary>Opt-in: exports OTel traces/metrics via OTLP. Default false (no exporter).</summary>
    public bool OtlpEnabled { get; init; }

    /// <summary>Skips the LLM call when an iteration adds zero reviewable lines and has no
    /// open threads (lockfile-only churn, deletions-only) — the run publishes a clean vote
    /// from a synthetic result. Default true; set false to restore the pre-skip behavior.</summary>
    public bool TrivialDiffSkipEnabled { get; init; } = true;

    /// <summary>Map-reduce sharding for large diffs (v1: no merge pass). Disabled by default;
    /// small PRs always take the exact legacy single-agent path.</summary>
    public ShardingOptions Sharding { get; init; } = new();

    /// <summary>
    /// In-flight shells (runs persisted by BeginRunStage but never finalized — crash or kill
    /// between stages 75 and 100) older than this at startup are reaped and finalized as
    /// failures, so a crashed run blocks the head for at most this window plus normal
    /// failure backoff. Default 10 minutes.
    /// </summary>
    public int StaleShellMinutes { get; init; } = 10;

    /// <summary>Finding-store retention (P2-26); pruned at the discovery-sweep tail.</summary>
    public RetentionOptions Retention { get; init; } = new();

    /// <summary>Finding-store storage tuning (journal mode escape hatch).</summary>
    public StoreOptions Store { get; init; } = new();
}

/// <summary>Finding-store storage tuning.</summary>
public sealed class StoreOptions
{
    /// <summary>Wal (default) | Delete. WAL requires POSIX advisory locks — keep the
    /// database on local disk; use Delete on network filesystems.</summary>
    public string JournalMode { get; init; } = "Wal";
}

/// <summary>Agent repo-scan budgets (P2-28): one Grep tool call aborts with a truncation
/// marker when either aggregate budget is reached. Bound from the "RepoReadTools" config
/// section (e.g. <c>RepoReadTools:GrepMaxMs</c>, <c>RepoReadTools__GrepMaxLines</c>).</summary>
public sealed class RepoReadToolsOptions
{
    public const string SectionName = "RepoReadTools";

    /// <summary>Aggregate wall-clock budget (ms) for one Grep call; default 10 s.</summary>
    [Range(1, 600_000)]
    public int GrepMaxMs { get; init; } = RepoReadTools.DefaultGrepMaxMs;

    /// <summary>Aggregate line budget for one Grep call; default 200k lines.</summary>
    [Range(1, 10_000_000)]
    public int GrepMaxLines { get; init; } = RepoReadTools.DefaultGrepMaxLines;
}

/// <summary>Bounds finding-store growth: old runs are deleted at the discovery-sweep tail
/// (at most once per hour), keeping dedupe continuity intact.</summary>
public sealed class RetentionOptions
{
    /// <summary>Runs started more than this many days ago are pruned. The latest completed
    /// run and the last <see cref="MinRunsPerPr"/> runs of a PR are always kept.</summary>
    [Range(1, 3650)]
    public int Days { get; init; } = 30;

    /// <summary>Minimum runs kept per PR regardless of age.</summary>
    [Range(1, 500)]
    public int MinRunsPerPr { get; init; } = 5;
}

public sealed class ReviewPipelineFactory(
    IPullRequestSource source,
    IFindingStore store,
    RepoCheckoutPool checkoutPool,
    IChatClientFactory chatClientFactory,
    IOptions<ReviewForgeServiceOptions> options,
    IOptions<RepoReadToolsOptions> repoReadToolsOptions,
    ILoggerFactory loggerFactory,
    IContextEnricher? enricher = null,
    TimeProvider? clock = null,
    IEnumerable<IFindingFixer>? findingFixers = null,
    IFixVerifier? fixVerifier = null,
    IOptions<AutoFixOptions>? autoFixOptions = null)
{
    public ReviewPipeline Create()
    {
        var opts = options.Value;
        var repoReadToolsOpts = repoReadToolsOptions.Value;
        var cleanVote = opts.CleanRunVote.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? (ReviewerVote?)null
            : Enum.TryParse<ReviewerVote>(opts.CleanRunVote, ignoreCase: true, out var parsedVote)
              && parsedVote is ReviewerVote.NoResponse or ReviewerVote.Approved or ReviewerVote.ApprovedWithSuggestions
                ? parsedVote
                : throw new InvalidOperationException(
                    $"ReviewForge:CleanRunVote '{opts.CleanRunVote}' is invalid; expected NoResponse | Approved | ApprovedWithSuggestions | None");
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
        var verifier = fixVerifier ?? NullFixVerifier.Instance;
        var factoryLogger = loggerFactory.CreateLogger<ReviewPipelineFactory>();
        foreach (var ruleId in autoFix.AllowedRuleIds)
        {
            if (!registry.TryGet(ruleId, out _))
            {
                factoryLogger.LogWarning(
                    "AutoFix:AllowedRuleIds entry '{RuleId}' has no registered fixer — it can never match", ruleId);
            }
        }

        var findingsDir = Path.Combine(opts.WorkDir, "findings");

        var diffBudget = new DiffBudget(
            opts.MaxDiffBytes,
            opts.MaxDiffBytesPerFile,
            opts.DiffExcludeGlobs ?? DiffBudget.Default.ExcludeGlobs);

        IReviewStage[] stages =
        [
            new FetchPrContextStage(source, store),
            new ReviewGateStage(clock),
            new PrepareRepositoryStage(
                checkoutPool,
                loggerFactory.CreateLogger<PrepareRepositoryStage>(),
                diffBudget,
                source,
                enricher,
                clock),
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
            new AutoFixFindingsStage(
                registry, agent, verifier, autoFix, loggerFactory.CreateLogger<AutoFixFindingsStage>(),
                store: store),
            new BeginRunStage(store, clock),
            new TriageThreadsStage(source, loggerFactory.CreateLogger<TriageThreadsStage>()),
            new PublishFindingsStage(source, store, loggerFactory.CreateLogger<PublishFindingsStage>(), cleanVote),
            new PersistRunStage(store, clock),
        ];

        return new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>());
    }
}