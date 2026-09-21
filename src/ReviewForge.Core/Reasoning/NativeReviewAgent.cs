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
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Reasoning.Rules;

/// <summary>Tuning knobs for the agent loop.</summary>
public sealed record AgentOptions
{
    public int MaxContextTokens { get; init; } = 150_000;
    public int MaxIterations { get; init; } = 30;
    public int ReadMaxLines { get; init; } = RepoReadTools.DefaultMaxLines;
    public string? PromptOverridePath { get; init; }
    public string? RuleSetsPath { get; init; }
    public IEnumerable<string>? DenyPatterns { get; init; }
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
        => CreateAgent(collector, contextStore, repoDir, ruleBook, null, null, null);

    private AIAgent CreateAgent(
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        TokenUsage? usage,
        IReadOnlySet<string>? changedFiles,
        DiffIndex? diff)
    {
        var repoTools = new RepoReadTools(repoDir, _Options.DenyPatterns, _Options.ReadMaxLines);
        var reviewTools = new ReviewTools(collector, contextStore, ruleBook, changedFiles, diff);
        IChatClient guarded = new TaskDoneGuardChatClient(collector, chatClientFactory.Create());
        IChatClient invoking = new ChatClientBuilder(guarded)
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = _Options.MaxIterations)
            .Build();
        IChatClient tracked = new UsageTrackingChatClient(invoking, usage ?? new TokenUsage(), _Logger, _Options.DebugLogging);
        return tracked.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "reviewforge-native",
            ChatOptions = new ChatOptions
            {
                ModelId = chatClientFactory.ModelName,
                Instructions = SystemPromptComposer.Compose(_Options.PromptOverridePath, ruleBook),
                Reasoning = _Options.Effort is { } effort ? new ReasoningOptions {Effort = effort} : null,
                Tools =
                [
                    AIFunctionFactory.Create(repoTools.ReadFile), AIFunctionFactory.Create(repoTools.List),
                    AIFunctionFactory.Create(repoTools.Grep), AIFunctionFactory.Create(reviewTools.ReadContext),
                    AIFunctionFactory.Create(reviewTools.GetRulebook), AIFunctionFactory.Create(reviewTools.RecordFinding),
                    AIFunctionFactory.Create(reviewTools.RecordUncertainty), AIFunctionFactory.Create(reviewTools.TaskDone),
                ],
            },
            AIContextProviders = [new CompactionProvider(new SlidingWindowCompactionStrategy(CompactionTriggers.TokensExceed(_Options.MaxContextTokens)))],
        });
    }

    public Task<ReviewResult> RunAsync(string userPrompt, ReviewCollector collector, ContextStore contextStore, string repoDir, CancellationToken ct)
        => RunAsync(userPrompt, collector, contextStore, repoDir, null, null, null, ct);

    public Task<ReviewResult> RunAsync(
        string userPrompt,
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        CancellationToken ct)
        => RunAsync(userPrompt, collector, contextStore, repoDir, ruleBook, null, null, ct);

    public async Task<ReviewResult> RunAsync(
        string userPrompt,
        ReviewCollector collector,
        ContextStore contextStore,
        string repoDir,
        RuleBook? ruleBook,
        IReadOnlySet<string>? changedFiles,
        DiffIndex? diff,
        CancellationToken ct)
    {
        var usage = new TokenUsage();
        var agent = CreateAgent(collector, contextStore, repoDir, ruleBook, usage, changedFiles, diff);
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
                yield return update;
            }
        }
    }
}