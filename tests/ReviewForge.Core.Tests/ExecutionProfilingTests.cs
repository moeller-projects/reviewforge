using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Profiling;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class ExecutionProfilingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Retries_have_distinct_attempts_one_logical_call_and_no_usage_double_count(bool streaming, bool exhaust)
    {
        var failure = new InvalidOperationException("secret source");
        var raw = new ScriptedChatClient { ThrowOnResponse = failure, StreamingUpdates = [], ThrowOnStream = failure };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "VerifyFindings", ModelCallProfile.Verifier);
        var retries = 0;
        var client = new RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => {
            retries++;
            if (!exhaust) { raw.ThrowOnResponse = null; raw.ThrowOnStream = null; }
            return true;
        });
        var observed = await Record.ExceptionAsync(async () => {
            if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
            else await client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);
        });
        Assert.Same(exhaust ? failure : null, observed);
        var snapshot = collector.Snapshot();
        var logical = Assert.Single(snapshot.Activities!, a => a.Kind == "logical");
        var attempts = snapshot.Activities!.Where(a => a.Kind == "attempt").ToArray();
        Assert.Equal(exhaust ? 3 : 2, attempts.Length);
        Assert.Equal(attempts.Length, snapshot.ObservedCalls);
        Assert.Equal(exhaust ? "failed" : "completed", logical.Outcome);
        Assert.Equal(exhaust ? 2 : 1, retries);
        Assert.All(attempts, a => { Assert.Equal(logical.Id, a.ParentId); Assert.Equal(logical.Id, a.LogicalCallId); });
        Assert.Equal(Enumerable.Range(1, attempts.Length), attempts.Select(a => a.AttemptNumber!.Value));
        Assert.All(attempts.SkipLast(1), a => { Assert.Equal("failed", a.Outcome); Assert.True(a.BackoffMilliseconds > 0); });
        Assert.All(snapshot.Calls, c => {
            Assert.Equal(logical.Id, c.LogicalCallId);
            Assert.Equal(attempts[c.AttemptNumber!.Value - 1].Id, c.AttemptId);
            Assert.Equal(c.AttemptId, c.ParentCallId);
            Assert.Equal("sdk-internal-unavailable", c.RetryCoverage);
        });
        Assert.Null(snapshot.Aggregate!.Usage!.InputTokens.Tokens);
        Assert.Equal(attempts.Length, snapshot.Aggregate.Activities!["attempt"].Observed);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_backoff_keeps_failed_attempt_and_cancelled_logical_call(bool streaming)
    {
        using var cancelled = new CancellationTokenSource();
        var original = new InvalidOperationException("secret");
        var raw = new ScriptedChatClient { ThrowOnResponse = original, StreamingUpdates = [], ThrowOnStream = original };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
        var client = new RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => { cancelled.Cancel(); return true; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: cancelled.Token)) { }
            else await client.GetResponseAsync([], cancellationToken: cancelled.Token);
        });
        var snapshot = collector.Snapshot();
        Assert.Equal("failed", Assert.Single(snapshot.Calls).Outcome);
        Assert.Equal("failed", Assert.Single(snapshot.Activities!, a => a.Kind == "attempt").Outcome);
        Assert.NotNull(Assert.Single(snapshot.Activities!, a => a.Kind == "attempt").BackoffMilliseconds);
        Assert.Equal("cancelled", Assert.Single(snapshot.Activities!, a => a.Kind == "logical").Outcome);
    }

    [Fact]
    public async Task Synchronous_stream_creation_failure_and_nontransient_failure_remain_original_failures()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
        var raw = new ScriptedChatClient();
        var client = new RetryingChatClient(raw, _ => false);
        await Assert.ThrowsAsync<NotSupportedException>(async () => {
            await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        var original = new InvalidOperationException("secret");
        raw.ThrowOnResponse = original;
        Assert.Same(original, await Record.ExceptionAsync(() => client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)));
        Assert.All(collector.Snapshot().Activities!, a => Assert.Equal("failed", a.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_is_not_retried_and_original_exception_survives(bool streaming)
    {
        var original = new OperationCanceledException("secret");
        var raw = new ScriptedChatClient { ThrowOnResponse = original, StreamingUpdates = [], ThrowOnStream = original };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
        var client = new RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => throw new Exception("must not inspect cancellation"));
        var actual = await Record.ExceptionAsync(async () => {
            if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
            else await client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);
        });
        Assert.Same(original, actual);
        Assert.Equal("cancelled", Assert.Single(collector.Snapshot().Calls).Outcome);
        Assert.All(collector.Snapshot().Activities!, a => Assert.Equal("cancelled", a.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_failure_after_update_never_retries_and_early_disposal_is_interrupted(bool fail)
    {
        var original = new InvalidOperationException("secret");
        var raw = new ScriptedChatClient { StreamingUpdates = [new(ChatRole.Assistant, "secret")], ThrowOnStream = fail ? original : null };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning", ModelCallProfile.Review);
        var client = new RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => throw new Exception("must not retry"));
        var actual = await Record.ExceptionAsync(async () => {
            await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken))
                if (!fail) break;
        });
        Assert.Same(fail ? original : null, actual);
        Assert.Equal(1, collector.Snapshot().ObservedCalls);
        Assert.All(collector.Snapshot().Activities!, a => Assert.Equal(fail ? "failed" : "interrupted", a.Outcome));
    }

    [Fact]
    public async Task Tool_wrapper_preserves_schema_arguments_result_token_and_exception_without_a_parent()
    {
        object? seen = null;
        var sentinel = new object();
        var token = TestContext.Current.CancellationToken;
        var original = new InvalidOperationException("secret error");
        var fail = false;
        var inner = AIFunctionFactory.Create((string source, CancellationToken ct) => {
            Assert.Equal(token, ct); seen = source;
            if (fail) throw original;
            return sentinel;
        }, "ReadFile");
        var wrapped = new ProfiledAIFunction(inner);
        Assert.Equal(inner.Name, wrapped.Name);
        Assert.Equal(inner.Description, wrapped.Description);
        Assert.Equal(inner.JsonSchema.GetRawText(), wrapped.JsonSchema.GetRawText());
        Assert.Equal(inner.ReturnJsonSchema, wrapped.ReturnJsonSchema);
        Assert.Same(inner.JsonSerializerOptions, wrapped.JsonSerializerOptions);
        Assert.Equal(inner.UnderlyingMethod, wrapped.UnderlyingMethod);
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning", ModelCallProfile.Review);
        var args = new AIFunctionArguments(new Dictionary<string, object?> { ["source"] = "secret-path-and-source" });
        var expected = await inner.InvokeAsync(args, token);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await wrapped.InvokeAsync(args, token)));
        Assert.Equal("secret-path-and-source", seen);
        fail = true;
        Assert.Same(original, await Record.ExceptionAsync(async () => await wrapped.InvokeAsync(args, token)));
        Assert.Equal(new[] { "completed", "failed" }, collector.Snapshot().Activities!.Select(a => a.Outcome));
        Assert.All(collector.Snapshot().Activities!, a => Assert.Equal("missing-parent", a.ParentCoverage));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(collector.Snapshot()));
        Assert.Equal(0, collector.Snapshot().ObservedCalls);
    }

    [Fact]
    public async Task Concurrent_tools_are_run_local_and_do_not_retain_unknown_tool_names_or_cancellation_messages()
    {
        var root = new ModelProfileCollector(Guid.NewGuid());
        using var rootScope = ModelCallContext.Enter(root, "ExecuteReasoning");
        var wrapped = new ProfiledAIFunction(AIFunctionFactory.Create(async (CancellationToken ct) => {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); return "secret-result";
        }, "secret-tool-name"));
        var collectors = Enumerable.Range(0, 20).Select(_ => new ModelProfileCollector(Guid.NewGuid())).ToArray();
        await Task.WhenAll(collectors.Select(async collector => {
            using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await wrapped.InvokeAsync(new(), TestContext.Current.CancellationToken)));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wrapped.InvokeAsync(new(), cancelled.Token));
            Assert.Equal(5, collector.Snapshot().Activities!.Count);
            Assert.All(collector.Snapshot().Activities!, a => Assert.Equal(collector.Snapshot().RunId, a.RunId));
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(collector.Snapshot()));
        }));
        Assert.Empty(root.Snapshot().Activities!);
        Assert.Same(root, ModelCallContext.Current!.Collector);
    }

    [Fact]
    public async Task Concurrent_function_dispatch_with_reused_provider_ids_links_to_its_own_invocation()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(async index => {
            var response = ScriptedChatClient.FunctionCalls(("ReadFile", new Dictionary<string, object?>()), ("ReadFile", new Dictionary<string, object?>()));
            // Both calls intentionally have the same provider name/id shape across independent invocations.
            var raw = new ScriptedChatClient(response);
            var function = new ProfiledAIFunction(AIFunctionFactory.Create(async () => {
                var invocation = ModelCallContext.Current!.InvocationId;
                Assert.NotNull(invocation);
                await Task.Yield();
                Assert.Equal(invocation, ModelCallContext.Current!.InvocationId);
                return "secret-result";
            }, "ReadFile"));
            var invoking = new ChatClientBuilder(new RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => false))
                .UseFunctionInvocation(configure: c => c.AllowConcurrentInvocation = true).Build();
            var client = new ModelProfileChatClient(invoking, index % 2 == 0 ? ModelCallProfile.Review : ModelCallProfile.Fix);
            await client.GetResponseAsync([], new ChatOptions { Tools = [function] }, TestContext.Current.CancellationToken);
        }));
        var snapshot = collector.Snapshot();
        var tools = snapshot.Activities!.Where(a => a.Kind == "tool").ToArray();
        Assert.Equal(20, tools.Length);
        Assert.All(tools, tool => {
            var parent = Assert.Single(snapshot.Calls, c => c.CallId == tool.ParentId);
            Assert.Equal(parent.Profile, tool.Profile);
            Assert.Equal(parent.LogicalCallId, tool.LogicalCallId);
            Assert.Equal(parent.AttemptId, tool.AttemptId);
        });
        Assert.All(tools.GroupBy(t => t.ParentId), group => Assert.Equal(2, group.Count()));
        Assert.Null(ModelCallContext.Current!.InvocationId);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot));
    }

    public static IEnumerable<object[]> CoverageMatrix()
    {
        foreach (var kind in new[] { "Review", "ReviewDraft", "Resolve" })
        foreach (var streaming in new[] { false, true })
        {
            if (kind != "Resolve") yield return [kind, "ExecuteReasoning", ToolProfile.Review, streaming];
            if (kind == "Review") yield return [kind, "AutoFixFindings", ToolProfile.Fix, streaming];
            if (kind == "Resolve") yield return [kind, "TriageComments", ToolProfile.Triage, streaming];
            if (kind == "Resolve") yield return [kind, "ApplyFixes", ToolProfile.Fix, streaming];
        }
    }

    [Theory]
    [MemberData(nameof(CoverageMatrix))]
    public async Task Registered_tool_matrix_preserves_behavior_and_correlates_every_dispatch(string kind, string stage, ToolProfile profile, bool streaming)
    {
        var root = Path.Combine(Path.GetTempPath(), $"profile-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.cs"), "class A {}\n");
        try
        {
            var calls = new List<(string Name, IDictionary<string, object?> Args)>();
            if (profile == ToolProfile.Fix)
            {
                calls.Add(("ReadFileWithHashes", new Dictionary<string, object?> { ["path"] = "A.cs" }));
                calls.Add(("EditFile", new Dictionary<string, object?> { ["path"] = "A.cs", ["edits"] = new[] {
                    new ReviewForge.Core.Editing.LineEdit(ReviewForge.Core.Analysis.HashLine.Of("class A {}"), null, null, null, "class B {}") } }));
            }
            else
            {
                calls.Add(("ReadFile", new Dictionary<string, object?> { ["path"] = "A.cs" }));
                calls.Add(("List", new Dictionary<string, object?>()));
                calls.Add(("Grep", new Dictionary<string, object?> { ["pattern"] = "class" }));
                calls.Add(("repo_file_diff", new Dictionary<string, object?> { ["path"] = "A.cs" }));
                calls.Add(("FindReferences", new Dictionary<string, object?> { ["identifier"] = "A" }));
                calls.Add(("ReadContext", new Dictionary<string, object?> { ["name"] = "secret" }));
                if (profile == ToolProfile.Review)
                {
                    calls.Add(("GetRulebook", new Dictionary<string, object?>()));
                    calls.Add(("RecordFinding", new Dictionary<string, object?> { ["ruleId"] = "null-deref", ["title"] = "secret", ["severity"] = "high",
                        ["category"] = "bug", ["description"] = "secret", ["snippet"] = "class A {}", ["filePath"] = "A.cs", ["startLine"] = 1 }));
                    calls.Add(("RecordUncertainty", new Dictionary<string, object?> { ["topic"] = "secret", ["question"] = "secret" }));
                }
                else calls.Add(("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 1, ["verdict"] = "OutOfScope", ["evidence"] = "secret", ["confidence"] = "low" }));
            }
            calls.Add(("TaskDone", new Dictionary<string, object?> { [profile == ToolProfile.Triage ? "summary" : "reviewSummary"] = "secret" }));
            var response = ScriptedChatClient.FunctionCalls(calls.ToArray());
            var raw = new ScriptedChatClient(response) { StreamingUpdates = [new(ChatRole.Assistant, response.Messages[0].Contents)] };
            var collector = new ModelProfileCollector(Guid.NewGuid());
            using var scope = ModelCallContext.Enter(collector, stage);
            var request = new AgentRunRequest("secret", new(), new(), root, profile,
                AllowedThreadIds: profile == ToolProfile.Triage ? new HashSet<long> { 1 } : null,
                WritablePaths: profile == ToolProfile.Fix ? new HashSet<string> { "A.cs" } : null);
            var agent = new NativeReviewAgent(new FakeChatClientFactory(raw)).CreateAgent(request);
            if (streaming) await foreach (var _ in agent.RunStreamingAsync("secret", cancellationToken: TestContext.Current.CancellationToken)) { }
            else await agent.RunAsync("secret", cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(request.Collector.Done, $"{kind}/{stage}/{streaming}");
            var snapshot = collector.Snapshot();
            var model = Assert.Single(snapshot.Calls);
            var tools = snapshot.Activities!.Where(a => a.Kind == "tool").ToArray();
            Assert.Equal(calls.Select(c => c.Name).Order(), tools.Select(t => t.ToolName).Order());
            Assert.All(tools, t => {
                Assert.Equal(model.CallId, t.ParentId);
                Assert.Equal(model.LogicalCallId, t.LogicalCallId);
                Assert.Equal(model.AttemptId, t.AttemptId);
                Assert.Equal(model.Profile, t.Profile);
                Assert.Equal(stage, t.Stage);
                Assert.Equal("completed", t.Outcome);
                Assert.Equal("observed", t.ParentCoverage);
            });
            Assert.Equal(calls.Count, snapshot.Aggregate!.Activities!["tool"].Observed);
            Assert.Equal(1, snapshot.Aggregate.ObservedCalls);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot));
            if (profile == ToolProfile.Fix) Assert.Contains("class B", File.ReadAllText(Path.Combine(root, "A.cs")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_collector_use_cannot_borrow_another_runs_ambient_parent()
    {
        var first = new ModelProfileCollector(Guid.NewGuid());
        var second = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(first, "ExecuteReasoning", ModelCallProfile.Review);
        using var call = ModelCallContext.EnterCall(Guid.NewGuid(), Guid.NewGuid(), 1);
        second.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5").Complete("completed", null);
        second.BeginActivity("tool", "TaskDone").Complete("completed");
        var snapshot = second.Snapshot();
        Assert.Null(Assert.Single(snapshot.Calls).LogicalCallId);
        Assert.Null(snapshot.Calls[0].AttemptId);
        var tool = Assert.Single(snapshot.Activities!);
        Assert.Null(tool.LogicalCallId);
        Assert.Null(tool.AttemptId);
        Assert.Equal("unavailable", tool.Stage);
        Assert.Equal(ModelCallProfile.Unspecified, tool.Profile);
        Assert.Empty(first.Snapshot().Calls);
    }

    [Fact]
    public void Shared_cap_and_stage_summaries_do_not_relabel_tools_as_models_and_resume_updates_all_ids()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid(), 3);
        using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
        var logical = collector.BeginActivity("logical");
        using var callScope = ModelCallContext.EnterCall(logical.Id);
        var attempt = collector.BeginActivity("attempt", attemptNumber: 1);
        using var attemptScope = ModelCallContext.EnterCall(logical.Id, attempt.Id, 1, attempt);
        collector.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5").Complete("completed", new() { InputTokenCount = 2 });
        for (var i = 0; i < 10_010; i++) collector.BeginActivity("tool", "EditFile").Complete("completed");
        collector.BeginActivity("tool", "secret name");
        var id = Guid.NewGuid();
        collector.Resume(id);
        attempt.Wait(12);
        attempt.Wait(4, backoff: true);
        attempt.Complete("failed");
        attempt.Complete("completed");
        logical.Complete("cancelled");
        var snapshot = collector.Snapshot();
        Assert.Equal(3, snapshot.Calls.Count + snapshot.Activities!.Count);
        Assert.True(snapshot.Truncated);
        Assert.Equal(1, snapshot.ObservedCalls);
        Assert.Equal(2, snapshot.Aggregate!.Usage!.InputTokens.Tokens);
        var summary = snapshot.Aggregate.Activities!;
        Assert.Equal(10_011, summary["tool"].Observed);
        Assert.Equal(1, summary["tool"].Interrupted);
        Assert.Equal(10_010, summary["tool"].Completed);
        Assert.Equal(1, summary["attempt"].Failed);
        Assert.Equal(12, summary["attempt"].GovernorWaitMilliseconds);
        Assert.Equal(4, summary["attempt"].BackoffMilliseconds);
        Assert.Equal(summary["tool"], snapshot.Aggregate.Stages!["ApplyFixes"].Activities!["tool"]);
        Assert.All(snapshot.Activities!, a => Assert.Equal(id, a.ProfileId));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot));
    }
}
