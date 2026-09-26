using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning.Rules;

namespace ReviewForge.Core.Reasoning;

/// <summary>Tuning knobs for the agent loop.</summary>
public sealed record AgentOptions
{
    public int MaxContextTokens { get; init; } = 150_000;
    public int MaxIterations { get; init; } = 30;
    public int ReadMaxLines { get; init; } = RepoReadTools.DefaultMaxLines;
    public string? PromptOverridePath { get; init; }
    public string? RuleSetsPath { get; init; }
    public IEnumerable<string>? DenyPatterns { get; init; }
    /// <summary>Directory names Grep never descends into; null = defaults (bin, obj, node_modules, .git, .vs, packages).</summary>
    public IEnumerable<string>? GrepExcludeDirs { get; init; }

    /// <summary>Aggregate wall-clock budget (ms) for one Grep tool call (P2-28).</summary>
    public int GrepMaxMs { get; init; } = RepoReadTools.DefaultGrepMaxMs;

    /// <summary>Aggregate line budget for one Grep tool call (P2-28).</summary>
    public int GrepMaxLines { get; init; } = RepoReadTools.DefaultGrepMaxLines;
    public ReasoningEffort? Effort { get; init; }
    public bool DebugLogging { get; init; }
}

/// <summary>Builds and runs the native review agent with read and review tools.</summary>
public sealed class NativeReviewAgent(
    IChatClientFactory chatClientFactory,
    AgentOptions? options = null,
    ILogger<NativeReviewAgent>? logger = null)
{
    private readonly ILogger<NativeReviewAgent>? _Logger = logger;
    private readonly AgentOptions _Options = options ?? new AgentOptions();

    public RuleBook ComposeRuleBook(IReadOnlyList<string> changedFiles, IReadOnlyList<string> repoRootFiles)
        => new RuleBookComposer().Compose(changedFiles, repoRootFiles, _Options.RuleSetsPath);

    public AIAgent CreateAgent(ReviewCollector collector, ContextStore contextStore, string repoDir, RuleBook? ruleBook = null)
        => CreateAgent(collector, contextStore, repoDir, ruleBook, null, null, null, null);

    private AIAgent CreateAgent(
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        TokenUsage? usage,
        IReadOnlySet<string>? changedFiles,
        DiffIndex? diff,
        IReadOnlySet<string>? resolvedKeys,
        IReadOnlyList<AITool>? extraTools = null)
    {
        var repoTools = new RepoReadTools(
            repoDir, _Options.DenyPatterns, _Options.ReadMaxLines, _Options.GrepExcludeDirs,
            grepMaxMs: _Options.GrepMaxMs, grepMaxLines: _Options.GrepMaxLines);
        var reviewTools = new ReviewTools(collector, contextStore, ruleBook, changedFiles, diff, resolvedKeys: resolvedKeys);
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(repoTools.ReadFile), AIFunctionFactory.Create(repoTools.List),
            AIFunctionFactory.Create(repoTools.Grep), AIFunctionFactory.Create(reviewTools.ReadContext),
            AIFunctionFactory.Create(reviewTools.GetRulebook), AIFunctionFactory.Create(reviewTools.RecordFinding),
            AIFunctionFactory.Create(reviewTools.RecordUncertainty), AIFunctionFactory.Create(reviewTools.TaskDone),
        };
        if (extraTools is not null)
        {
            tools.AddRange(extraTools);
        }

        var tracked = CreatePipeline(collector, usage ?? new TokenUsage());
        return tracked.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "reviewforge-native",
            ChatOptions = new ChatOptions
            {
                ModelId = chatClientFactory.ModelName,
                Instructions = SystemPromptComposer.Compose(_Options.PromptOverridePath, ruleBook),
                Reasoning = _Options.Effort is { } effort ? new ReasoningOptions {Effort = effort} : null,
                Tools = tools,
            },
            AIContextProviders = [new CompactionProvider(new SlidingWindowCompactionStrategy(CompactionTriggers.TokensExceed(_Options.MaxContextTokens)))],
        });
    }

    /// <summary>TaskDone-guarded, function-invoking, usage-tracked client pipeline shared by
    /// the review run and the fix pass.</summary>
    private IChatClient CreatePipeline(ReviewCollector collector, TokenUsage usage)
    {
        IChatClient guarded = new TaskDoneGuardChatClient(collector, chatClientFactory.Create());
        IChatClient invoking = new ChatClientBuilder(guarded)
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = _Options.MaxIterations)
            .Build();
        return new UsageTrackingChatClient(invoking, usage, _Logger, _Options.DebugLogging);
    }

    public Task<ReviewResult> RunAsync(string userPrompt, ReviewCollector collector, ContextStore contextStore, string repoDir, CancellationToken ct)
        => RunAsync(userPrompt, collector, contextStore, repoDir, null, null, null, null, ct);

    public Task<ReviewResult> RunAsync(
        string userPrompt,
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        CancellationToken ct)
        => RunAsync(userPrompt, collector, contextStore, repoDir, ruleBook, null, null, null, ct);

    public async Task<ReviewResult> RunAsync(
        string userPrompt,
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        IReadOnlySet<string>? changedFiles,
        DiffIndex? diff,
        IReadOnlySet<string>? resolvedKeys,
        CancellationToken ct)
    {
        var usage = new TokenUsage();
        var agent = CreateAgent(collector, contextStore, repoDir, ruleBook, usage, changedFiles, diff, resolvedKeys);
        await agent.RunAsync(userPrompt, cancellationToken: ct);
        _Logger?.LogInformation("review agent token usage: input={InputTokens}, output={OutputTokens}, total={TotalTokens}", usage.InputTokens, usage.OutputTokens, usage.TotalTokens);
        var modelTag = new TagList { { "model", chatClientFactory.ModelName } };
        ReviewForgeTelemetry.AgentIterations.Record(usage.Turns, modelTag);
        if (!collector.Done)
        {
            ReviewForgeTelemetry.AgentTaskDoneMissing.Add(1, modelTag);
        }

        return collector.ToResult(collector.Done ? "agentic tool loop" : "iteration cap reached — task_done missing", ruleBook?.VersionHash);
    }

    /// <summary>
    /// Runs a constrained fix pass for one author-commanded "/rf fix": the agent gets ONLY
    /// the hash-line editor tools (ReadFileWithHashes, EditFile) plus TaskDone — no
    /// findings tools, no Grep — inside the same sandbox with a one-file writable set.
    /// The returned <see cref="FixPassResult"/> exposes the editor so the caller can read
    /// the merged session change and revert the file afterwards.
    /// </summary>
    public async Task<FixPassResult> RunWithEditToolsAsync(
        string userPrompt,
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        IReadOnlySet<string> writablePaths,
        int maxIterations,
        CancellationToken ct)
    {
        var editor = new HashLineEditor(new RepoPathGuard(repoDir), writablePaths);
        var reviewTools = new ReviewTools(collector, contextStore);
        var usage = new TokenUsage();
        IChatClient guarded = new TaskDoneGuardChatClient(collector, chatClientFactory.Create());
        IChatClient invoking = new ChatClientBuilder(guarded)
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
            .Build();
        IChatClient tracked = new UsageTrackingChatClient(invoking, usage, _Logger, _Options.DebugLogging);
        var agent = tracked.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "reviewforge-fix",
            ChatOptions = new ChatOptions
            {
                ModelId = chatClientFactory.ModelName,
                Instructions = SystemPromptComposer.Compose(_Options.PromptOverridePath, null),
                Reasoning = _Options.Effort is { } effort ? new ReasoningOptions {Effort = effort} : null,
                Tools =
                [
                    AIFunctionFactory.Create(editor.ReadFileWithHashes),
                    AIFunctionFactory.Create(editor.EditFile),
                    AIFunctionFactory.Create(reviewTools.TaskDone),
                ],
            },
            AIContextProviders = [new CompactionProvider(new SlidingWindowCompactionStrategy(CompactionTriggers.TokensExceed(_Options.MaxContextTokens)))],
        });
        await agent.RunAsync(userPrompt, cancellationToken: ct);
        _Logger?.LogInformation(
            "fix pass token usage: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens);
        if (!collector.Done)
        {
            ReviewForgeTelemetry.AgentTaskDoneMissing.Add(1, new TagList { { "model", chatClientFactory.ModelName } });
        }

        return new FixPassResult(
            collector.ToResult(collector.Done ? "agentic tool loop" : "iteration cap reached — task_done missing", null),
            editor,
            usage.InputTokens,
            usage.OutputTokens);
    }

    private sealed class TokenUsage
    {
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long TotalTokens { get; private set; }
        public int Turns { get; private set; }

        public void Add(UsageDetails? details)
        {
            Turns++;
            if (details is null) return;
            InputTokens += details.InputTokenCount ?? 0;
            OutputTokens += details.OutputTokenCount ?? 0;
            TotalTokens += details.TotalTokenCount ?? 0;
        }
    }

    private sealed class UsageTrackingChatClient(
        IChatClient inner,
        TokenUsage usage,
        ILogger? logger = null,
        bool debugArgs = false) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            usage.Add(response.Usage);
            var model = options?.ModelId ?? "default";
            ReviewForgeTelemetry.LlmRequests.Add(1, new TagList { { "model", model } });
            var inputTokens = response.Usage?.InputTokenCount ?? 0;
            var outputTokens = response.Usage?.OutputTokenCount ?? 0;
            if (inputTokens > 0)
            {
                ReviewForgeTelemetry.LlmTokens.Add(inputTokens, new TagList { { "token_type", "input" }, { "model", model } });
            }

            if (outputTokens > 0)
            {
                ReviewForgeTelemetry.LlmTokens.Add(outputTokens, new TagList { { "token_type", "output" }, { "model", model } });
            }

            logger?.LogDebug(
                "llm call: iteration tokens in={InputTokens} out={OutputTokens} elapsed={ElapsedMs}ms toolCalls={ToolCallCount}",
                response.Usage?.InputTokenCount ?? 0, response.Usage?.OutputTokenCount ?? 0,
                sw.ElapsedMilliseconds, response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
            if (debugArgs)
            {
                foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
                {
                    logger?.LogDebug("tool call {Tool} argsLength={ArgsLength}", call.Name,
                        call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments).Length);
                }
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Usage arrives as a UsageContent on the terminal streaming update; the
                // Codex provider is always streaming under the hood, so skipping this
                // would silently under-count tokens versus the GetResponseAsync path.
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                {
                    usage.Add(usageContent.Details);
                }

                yield return update;
            }
        }
    }
}