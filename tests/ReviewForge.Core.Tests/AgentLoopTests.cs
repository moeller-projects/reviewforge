using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class TaskDoneGuardTests
{
    [Fact]
    public async Task Passes_through_until_done_then_short_circuits()
    {
        var collector = new ReviewCollector();
        var inner = new ScriptedChatClient(ScriptedChatClient.Text("real answer"));
        var guard = new TaskDoneGuardChatClient(collector, inner);

        var first = await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("real answer", first.Text);
        Assert.Equal(1, inner.Calls);

        collector.Complete(new ReviewNarrative {ReviewSummary = "done"});
        var second = await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);
        Assert.Contains("already marked as done", second.Text);
        Assert.Equal(1, inner.Calls); // inner not called again
    }

    [Fact]
    public async Task Streaming_short_circuits_when_done()
    {
        var collector = new ReviewCollector();
        collector.Complete(new ReviewNarrative {ReviewSummary = "done"});
        var guard = new TaskDoneGuardChatClient(collector, new ScriptedChatClient());

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in guard.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "x")]))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
    }

    [Fact]
    public async Task Done_response_is_a_fresh_instance_per_call()
    {
        // The function invoker aggregates loop usage into the final response's message
        // contents. A shared static response would carry every prior run's usage into the
        // next run's token metrics — each short-circuit must return its own response.
        var collector = new ReviewCollector();
        var inner = new ScriptedChatClient();
        var guard = new TaskDoneGuardChatClient(collector, inner);
        collector.Complete(new ReviewNarrative {ReviewSummary = "done"});

        var first = await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "a")]);
        first.Messages[0].Contents.Add(new UsageContent(new UsageDetails {InputTokenCount = 5}));

        var second = await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "b")]);
        Assert.DoesNotContain(second.Messages[0].Contents, c => c is UsageContent);
        Assert.Contains("already marked as done", second.Text);
    }
}

public class AgentLoopTests : IDisposable
{
    private readonly string _RepoDir;

    public AgentLoopTests()
    {
        _RepoDir = Path.Combine(Path.GetTempPath(), "reviewforge-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_RepoDir);
        File.WriteAllText(Path.Combine(_RepoDir, "A.cs"), "class A {}\n");
    }

    public void Dispose() => Directory.Delete(_RepoDir, recursive: true);

    [Fact]
    public async Task Full_loop_records_finding_and_completes()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(
                ("RecordFinding", new Dictionary<string, object?>
                {
                    ["ruleId"] = "null-deref", ["title"] = "x may be null", ["severity"] = "high",
                    ["category"] = "bug", ["description"] = "deref", ["snippet"] = "class A {}",
                    ["filePath"] = "A.cs", ["startLine"] = 1,
                }),
                ("RecordUncertainty", new Dictionary<string, object?> {["topic"] = "t", ["question"] = "q"})),
            ScriptedChatClient.FunctionCalls(
                ("TaskDone", new Dictionary<string, object?>
                {
                    ["reviewSummary"] = "looks mostly fine",
                    ["acceptanceCriteria"] = new object[]
                    {
                        new Dictionary<string, object?> {["WorkItemId"] = 1, ["Criterion"] = "c", ["Status"] = "Met", ["Evidence"] = "e"},
                    },
                })));

        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
        var collector = new ReviewCollector();
        var result = await agent.RunAsync("review this", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.True(collector.Done);
        Assert.Equal("agentic tool loop", result.ReviewDepth);
        Assert.Single(result.Findings);
        Assert.Single(result.Uncertainties);
        Assert.Equal("looks mostly fine", result.Narrative.ReviewSummary);
    }

    [Fact]
    public async Task Token_usage_is_recorded_from_response_usage()
    {
        var withUsage = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-0-TaskDone", "TaskDone",
                new Dictionary<string, object?> { ["reviewSummary"] = "ok" })]))
        {
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 7, TotalTokenCount = 19 },
        };
        var script = new ScriptedChatClient(withUsage);
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
        var collector = new ReviewCollector();

        var result = await agent.RunAsync("review this", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.True(collector.Done);
        Assert.Equal("agentic tool loop", result.ReviewDepth);
    }

    [Fact]
    public async Task Cached_input_tokens_are_emitted_to_the_cached_metric()
    {
        var withCached = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-0-TaskDone", "TaskDone",
                new Dictionary<string, object?> { ["reviewSummary"] = "ok" })]))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 100, OutputTokenCount = 5, TotalTokenCount = 105,
                CachedInputTokenCount = 80,
            },
        };
        var script = new ScriptedChatClient(withCached);
        // Model tag scopes the listener: MeterListener measurement events are process-wide,
        // so a parallel test's emissions on this instrument would otherwise cross-talk.
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script, model: "cached-model"));
        var collector = new ReviewCollector();

        long cached = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.llm.tokens.cached_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "model" && (string?)tag.Value == "cached-model")
                {
                    Interlocked.Add(ref cached, measurement);
                }
            }
        });
        listener.Start();

        await agent.RunAsync("review this", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.True(collector.Done);
        Assert.Equal(80, Interlocked.Read(ref cached));
    }

    [Fact]
    public async Task Absent_cached_token_count_emits_no_cached_metric()
    {
        var withUsage = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-0-TaskDone", "TaskDone",
                new Dictionary<string, object?> { ["reviewSummary"] = "ok" })]))
        {
            Usage = new UsageDetails { InputTokenCount = 12, OutputTokenCount = 7, TotalTokenCount = 19 },
        };
        var script = new ScriptedChatClient(withUsage);
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script, model: "no-cached-model"));
        var collector = new ReviewCollector();

        var sawCachedMeasurement = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.llm.tokens.cached_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "model" && (string?)tag.Value == "no-cached-model")
                {
                    sawCachedMeasurement = true;
                }
            }
        });
        listener.Start();

        await agent.RunAsync("review this", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.True(collector.Done);
        Assert.False(sawCachedMeasurement); // unsupported provider → silence, not zero-spam
    }

    [Fact]
    public async Task Iteration_cap_without_task_done_flags_depth()
    {
        var noisy = Enumerable.Range(0, 10)
            .Select(_ => ScriptedChatClient.FunctionCalls(
                ("ReadContext", new Dictionary<string, object?> {["name"] = "nothing"})))
            .ToArray();
        var script = new ScriptedChatClient(noisy);

        var agent = new NativeReviewAgent(new FakeChatClientFactory(script), new AgentOptions {MaxIterations = 2});
        var collector = new ReviewCollector();
        var result = await agent.RunAsync("review this", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.False(collector.Done);
        Assert.Contains("task_done missing", result.ReviewDepth);
    }

    [Fact]
    public async Task Agent_uses_repo_tools_against_checkout()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(
                ("ReadFile", new Dictionary<string, object?> {["path"] = "A.cs"})),
            ScriptedChatClient.FunctionCalls(
                ("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "read the file"})));

        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
        var collector = new ReviewCollector();
        await agent.RunAsync("go", collector, new ContextStore(), _RepoDir, CancellationToken.None);

        // The tool result fed back into the loop contains the numbered file content.
        var toolMessage = script.Received.SelectMany(m => m).SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(toolMessage);
        Assert.Contains("class A", toolMessage.Result?.ToString());
    }

    [Fact]
    public async Task Effort_is_forwarded_to_chat_options()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script), new AgentOptions {Effort = ReasoningEffort.High});

        await agent.RunAsync("go", new ReviewCollector(), new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.Contains(script.ReceivedOptions, o => o?.Reasoning?.Effort == ReasoningEffort.High);
    }

    [Fact]
    public async Task Default_effort_leaves_reasoning_unset()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));

        await agent.RunAsync("go", new ReviewCollector(), new ContextStore(), _RepoDir, CancellationToken.None);

        Assert.NotEmpty(script.ReceivedOptions);
        Assert.All(script.ReceivedOptions, o => Assert.Null(o?.Reasoning));
    }
}