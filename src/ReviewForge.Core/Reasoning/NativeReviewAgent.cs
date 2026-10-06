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

/// <summary>Available tool and prompt contracts for an agent run.</summary>
public enum ToolProfile
{
    Review,
    Triage,
    Fix,
}

/// <summary>All inputs required to execute one native agent pass.</summary>
public sealed record AgentRunRequest(
    string UserPrompt,
    ReviewCollector Collector,
    ContextStore ContextStore,
    string RepoDir,
    ToolProfile Profile,
    RuleBook? RuleBook = null,
    IReadOnlySet<string>? ChangedFiles = null,
    DiffIndex? Diff = null,
    string? DiffText = null,
    IReadOnlySet<string>? ResolvedKeys = null,
    IReadOnlySet<long>? AllowedThreadIds = null,
    IReadOnlySet<string>? WritablePaths = null,
    int? MaxIterationsOverride = null,
    ChatTier Tier = ChatTier.Full);

/// <summary>Builds and runs the native review agent with profile-specific tools.</summary>
public sealed class NativeReviewAgent(
    IChatClientFactory chatClientFactory,
    AgentOptions? options = null,
    ILogger<NativeReviewAgent>? logger = null)
{
    private readonly ILogger<NativeReviewAgent>? _Logger = logger;
    private readonly AgentOptions _Options = options ?? new AgentOptions();

    public RuleBook ComposeRuleBook(IReadOnlyList<string> changedFiles, IReadOnlyList<string> repoRootFiles)
        => new RuleBookComposer().Compose(changedFiles, repoRootFiles, _Options.RuleSetsPath);

    public AIAgent CreateAgent(AgentRunRequest request)
    {
        Validate(request);
        return BuildAgent(request, new TokenUsage(), out _);
    }

    private AIAgent BuildAgent(AgentRunRequest request, TokenUsage usage, out HashLineEditor? editor)
    {
        editor = null;
        var tools = new List<AITool>();
        var instructions = request.Profile switch
        {
            ToolProfile.Review => SystemPromptComposer.Compose(_Options.PromptOverridePath, request.RuleBook),
            ToolProfile.Triage => SystemPromptComposer.ComposeTriage(),
            ToolProfile.Fix => SystemPromptComposer.ComposeFixPass(),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Profile)),
        };
        var effectiveTier = EffectiveTier(request);
        var maxIterations = request.MaxIterationsOverride ?? _Options.MaxIterations;

        if (request.Profile == ToolProfile.Fix)
        {
            editor = new HashLineEditor(new RepoPathGuard(request.RepoDir), request.WritablePaths!);
            tools.Add(AIFunctionFactory.Create(editor.ReadFileWithHashes));
            tools.Add(AIFunctionFactory.Create(editor.EditFile));
            var fixTools = new ReviewTools(request.Collector, request.ContextStore);
            tools.Add(AIFunctionFactory.Create(fixTools.TaskDone));
        }
        else
        {
            var repoTools = new RepoReadTools(
                request.RepoDir, _Options.DenyPatterns, _Options.ReadMaxLines, _Options.GrepExcludeDirs,
                grepMaxMs: _Options.GrepMaxMs, grepMaxLines: _Options.GrepMaxLines,
                diffText: request.DiffText, changedFiles: request.ChangedFiles, diff: request.Diff);
            tools.Add(AIFunctionFactory.Create(repoTools.ReadFile));
            tools.Add(AIFunctionFactory.Create(repoTools.List));
            tools.Add(AIFunctionFactory.Create(repoTools.Grep));
            tools.Add(AIFunctionFactory.Create(repoTools.FileDiff, "repo_file_diff"));
            tools.Add(AIFunctionFactory.Create(repoTools.FindReferences));
            var reviewTools = new ReviewTools(request.Collector, request.ContextStore,
                request.Profile == ToolProfile.Review ? request.RuleBook : null,
                request.ChangedFiles, request.Diff, resolvedKeys: request.ResolvedKeys);
            tools.Add(AIFunctionFactory.Create(reviewTools.ReadContext));
            if (request.Profile == ToolProfile.Review)
            {
                tools.Add(AIFunctionFactory.Create(reviewTools.GetRulebook));
                tools.Add(AIFunctionFactory.Create(reviewTools.RecordFinding));
                tools.Add(AIFunctionFactory.Create(reviewTools.RecordUncertainty));
                tools.Add(AIFunctionFactory.Create(reviewTools.TaskDone));
            }
            else
            {
                var triageTools = new TriageTools(request.Collector, request.AllowedThreadIds!);
                tools.Add(AIFunctionFactory.Create(triageTools.RecordVerdict));
                tools.Add(AIFunctionFactory.Create(triageTools.TaskDone));
            }
        }

        var tracked = CreatePipeline(request.Collector, usage, effectiveTier, maxIterations);
        return tracked.AsAIAgent(new ChatClientAgentOptions
        {
            Name = request.Profile switch
            {
                ToolProfile.Review => "reviewforge-native",
                ToolProfile.Triage => "reviewforge-triage",
                ToolProfile.Fix => "reviewforge-fix",
                _ => "reviewforge-native",
            },
            ChatOptions = new ChatOptions
            {
                ModelId = chatClientFactory.ModelName(effectiveTier),
                Instructions = instructions,
                Reasoning = _Options.Effort is { } effort ? new ReasoningOptions { Effort = effort } : null,
                Tools = tools,
            },
            AIContextProviders = [new CompactionProvider(new SlidingWindowCompactionStrategy(
                CompactionTriggers.TokensExceed(_Options.MaxContextTokens)))],
        });
    }

    private IChatClient CreatePipeline(ReviewCollector collector, TokenUsage usage, ChatTier tier, int maxIterations)
    {
        IChatClient guarded = new TaskDoneGuardChatClient(collector, chatClientFactory.Create(tier));
        IChatClient invoking = new ChatClientBuilder(guarded)
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = maxIterations)
            .Build();
        return new UsageTrackingChatClient(invoking, usage, _Logger, _Options.DebugLogging);
    }

    public async Task<ReviewResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        return (await RunCoreAsync(request, ct).ConfigureAwait(false)).Result;
    }

    private async Task<AgentRunOutcome> RunCoreAsync(AgentRunRequest request, CancellationToken ct)
    {
        Validate(request);
        var usage = new TokenUsage();
        var agent = BuildAgent(request, usage, out var editor);
        await agent.RunAsync(request.UserPrompt, cancellationToken: ct).ConfigureAwait(false);
        EmitTelemetry(request, usage);
        var result = request.Collector.ToResult(
            request.Collector.Done ? "agentic tool loop" : "iteration cap reached — task_done missing",
            request.RuleBook?.VersionHash);
        return new AgentRunOutcome(result, editor, request.Collector.ThreadVerdicts,
            usage.InputTokens, usage.OutputTokens);
    }

    private void EmitTelemetry(AgentRunRequest request, TokenUsage usage)
    {
        var tier = EffectiveTier(request);
        var modelTag = new TagList
        {
            {"model", chatClientFactory.ModelName(tier)},
            {"tier", tier.ToString().ToLowerInvariant()},
        };
        _Logger?.LogInformation("{Profile} agent token usage: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
            request.Profile, usage.InputTokens, usage.OutputTokens, usage.TotalTokens);
        LlmTelemetry.AgentIterations.Record(usage.Turns, modelTag);
        if (!request.Collector.Done) LlmTelemetry.AgentTaskDoneMissing.Add(1, modelTag);
    }

    private static ChatTier EffectiveTier(AgentRunRequest request)
        => request.Profile switch
        {
            ToolProfile.Review => request.Tier,
            ToolProfile.Triage => ChatTier.Full,
            ToolProfile.Fix => ChatTier.Fast,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Profile)),
        };

    public async Task<IReadOnlyList<ThreadVerdict>> RunTriageAsync(
        string userPrompt, ReviewCollector collector, ContextStore contextStore, string repoDir,
        IReadOnlySet<string>? changedFiles, DiffIndex? diff, string? diffText,
        IReadOnlySet<long>? allowedThreadIds, CancellationToken ct)
    {
        var request = new AgentRunRequest(userPrompt, collector, contextStore, repoDir, ToolProfile.Triage,
            ChangedFiles: changedFiles, Diff: diff, DiffText: diffText, AllowedThreadIds: allowedThreadIds);
        return (await RunCoreAsync(request, ct).ConfigureAwait(false)).Verdicts;
    }

    public async Task<FixPassResult> RunWithEditToolsAsync(
        string userPrompt, ReviewCollector collector, ContextStore contextStore, string repoDir,
        IReadOnlySet<string> writablePaths, int maxIterations, CancellationToken ct)
    {
        var request = new AgentRunRequest(userPrompt, collector, contextStore, repoDir, ToolProfile.Fix,
            WritablePaths: writablePaths, MaxIterationsOverride: maxIterations);
        var outcome = await RunCoreAsync(request, ct).ConfigureAwait(false);
        return new FixPassResult(outcome.Result, outcome.Editor!,
            outcome.UsageInputTokens, outcome.UsageOutputTokens);
    }

    private static void Validate(AgentRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserPrompt)) throw new ArgumentException("UserPrompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.RepoDir)) throw new ArgumentException("RepoDir is required.", nameof(request));
        switch (request.Profile)
        {
            case ToolProfile.Review when request.AllowedThreadIds is not null
                || request.WritablePaths is not null || request.MaxIterationsOverride is not null:
                throw new ArgumentException("Review profile cannot specify triage or fix-only fields.", nameof(request));
            case ToolProfile.Triage when request.AllowedThreadIds is null:
                throw new ArgumentException("Triage profile requires AllowedThreadIds.", nameof(request));
            case ToolProfile.Triage when request.WritablePaths is not null || request.MaxIterationsOverride is not null:
                throw new ArgumentException("Triage profile cannot specify fix-only fields.", nameof(request));
            case ToolProfile.Fix when request.WritablePaths is null:
                throw new ArgumentException("Fix profile requires WritablePaths.", nameof(request));
            case ToolProfile.Fix when request.AllowedThreadIds is not null:
                throw new ArgumentException("Fix profile cannot specify AllowedThreadIds.", nameof(request));
        }
    }

    private sealed record AgentRunOutcome(
        ReviewResult Result, HashLineEditor? Editor, IReadOnlyList<ThreadVerdict> Verdicts,
        long UsageInputTokens, long UsageOutputTokens);

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
            LlmTelemetry.LlmRequests.Add(1, new TagList {{"model", model}});
            var inputTokens = response.Usage?.InputTokenCount ?? 0;
            var outputTokens = response.Usage?.OutputTokenCount ?? 0;
            if (inputTokens > 0)
            {
                LlmTelemetry.LlmTokens.Add(inputTokens, new TagList {{"token_type", "input"}, {"model", model}});
            }

            if (outputTokens > 0)
            {
                LlmTelemetry.LlmTokens.Add(outputTokens, new TagList {{"token_type", "output"}, {"model", model}});
            }

            // Prompt-cache measurement: emitted only when the provider reports cached
            // input tokens — absence means "unsupported/unknown", never zero-spam.
            var cachedTokens = response.Usage?.CachedInputTokenCount ?? 0;
            if (cachedTokens > 0)
            {
                LlmTelemetry.LlmCachedTokens.Add(cachedTokens, new TagList {{"model", model}});
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
                    var cachedTokens = usageContent.Details?.CachedInputTokenCount ?? 0;
                    if (cachedTokens > 0)
                    {
                        LlmTelemetry.LlmCachedTokens.Add(
                            cachedTokens,
                            new TagList {{"model", options?.ModelId ?? "default"}});
                    }
                }

                yield return update;
            }
        }
    }
}