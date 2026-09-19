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