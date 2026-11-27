using Xunit;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Profiling;
using ReviewForge.Testing;

namespace ReviewForge.Core.Tests;

public class ModelProfilingTests(ITestOutputHelper output)
{
    [Fact]
    public void Collection_is_hard_bounded_and_measured_without_provider_IO()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid(), int.MaxValue);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 10_050; i++)
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var snapshot = collector.Snapshot();
        Assert.Equal(10_000, snapshot.Calls.Count);
        Assert.Equal(10_050, snapshot.ObservedCalls);
        Assert.Equal(10_050, snapshot.Aggregate!.CompletedCalls);
        Assert.Equal(0, snapshot.Aggregate.InterruptedCalls);
        Assert.True(snapshot.Truncated);
        Assert.Equal(snapshot.Calls.Count, snapshot.Calls.Select(c => c.CallId).Distinct().Count());
        output.WriteLine($"T10 collector-only 10050 calls: {elapsed.TotalMilliseconds:F3} ms; allocated {bytes} bytes; retained {snapshot.Calls.Count} metadata rows. Not a provider latency benchmark.");
    }
    [Fact]
    public void Retained_stage_summaries_include_usage_beyond_detail_cap_and_bound_stage_keys()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid(), 0);
        for (var i = 0; i < 10_001; i++)
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5")
                .Complete("completed", new() { InputTokenCount = 2, OutputTokenCount = 0 });
        collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
        collector.Begin("VerifyFindings", ModelCallProfile.Verifier, "openai", "gpt-5").Complete("incomplete", new() { InputTokenCount = 3 });
        collector.Begin("secret-path", ModelCallProfile.Fix, "openai", "gpt-5");
        var snapshot = collector.Snapshot();
        Assert.Empty(snapshot.Calls);
        var json = JsonSerializer.SerializeToElement(snapshot.Aggregate);
        Assert.True(json.TryGetProperty("Stages", out var stages), "Aggregate must retain stage summaries independently of details");
        Assert.Equal(3, stages.EnumerateObject().Count());
        var review = stages.GetProperty("ExecuteReasoning");
        Assert.Equal(10_002, review.GetProperty("ObservedCalls").GetInt64());
        Assert.Equal(10_002, review.GetProperty("CompletedCalls").GetInt64());
        var input = review.GetProperty("Usage").GetProperty("InputTokens");
        Assert.Equal(20_002, input.GetProperty("Tokens").GetInt64());
        Assert.Equal(10_001, input.GetProperty("ReportedCalls").GetInt64());
        Assert.Equal("partial", input.GetProperty("Coverage").GetString());
        Assert.Equal(0, review.GetProperty("Usage").GetProperty("OutputTokens").GetProperty("Tokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("Usage").GetProperty("TotalTokens").GetProperty("Tokens").ValueKind);
        Assert.Equal(1, stages.GetProperty("VerifyFindings").GetProperty("ProviderIncompleteCalls").GetInt64());
        Assert.Equal(1, stages.GetProperty("unavailable").GetProperty("InterruptedCalls").GetInt64());
        Assert.DoesNotContain("secret", json.GetRawText());
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("cancelled")]
    [InlineData("disposal-only")]
    public async Task Streaming_disposal_preserves_primary_failure_and_surfaces_standalone_disposal_error(string mode)
    {
        Exception? primary = mode == "transport" ? new InvalidOperationException("transport failed")
            : mode == "cancelled" ? new OperationCanceledException("transport cancelled") : null;
        var disposal = new IOException("dispose failed");
        var raw = new ScriptedChatClient { StreamingUpdates = [], ThrowOnStream = primary, ThrowOnStreamDispose = disposal };
        using var client = new ReviewForge.Core.Reasoning.RetryingChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), _ => false);
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning");
        var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        Assert.Same(primary ?? disposal, thrown);
        Assert.Equal(mode == "cancelled" ? "cancelled" : "failed", Assert.Single(collector.Snapshot().Calls).Outcome);
    }

    [Fact]
    public void Provider_incomplete_is_a_fully_observed_outcome_not_an_interrupted_capture()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid());
        collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("incomplete", null);
        Assert.Equal(0, collector.Snapshot().Aggregate!.InterruptedCalls);
    }

    [Fact]
    public void Reclaimed_run_reuses_profile_identity_without_fabricating_lost_calls()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid());
        var call = collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5");
        var id = Guid.NewGuid();
        collector.Resume(id);
        collector.Resume(id);
        call.Complete("completed", null);
        var snapshot = collector.Snapshot();
        Assert.True(snapshot.Incomplete);
        Assert.Equal(id, snapshot.ProfileId);
        Assert.Equal(id, Assert.Single(snapshot.Calls).ProfileId);
        Assert.Equal(1, snapshot.ObservedCalls);
    }

    [Fact]
    public void Aggregate_counts_include_omitted_and_interrupted_calls_and_completion_is_idempotent()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var collector = new ModelProfileCollector(Guid.NewGuid(), 1, time);
        var call = collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5");
        time.Advance(TimeSpan.FromSeconds(2));
        call.Complete("completed", new() { InputTokenCount = 3, OutputTokenCount = 2, TotalTokenCount = 5 });
        call.Complete("failed", null);
        collector.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5").Complete("cancelled", null);
        collector.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5");
        var snapshot = collector.Snapshot();
        Assert.Equal(3, snapshot.ObservedCalls);
        Assert.Equal(1, snapshot.Aggregate!.CompletedCalls);
        Assert.Equal(0, snapshot.Aggregate.FailedCalls);
        Assert.Equal(1, snapshot.Aggregate.CancelledCalls);
        Assert.Equal(1, snapshot.Aggregate.InterruptedCalls);
        Assert.Equal(2000, snapshot.Aggregate.ObservedDurationMilliseconds);
        Assert.Equal(2000, snapshot.Aggregate.Stages!["ExecuteReasoning"].ObservedDurationMilliseconds);
        Assert.Equal(1, snapshot.Aggregate.Stages["ApplyFixes"].CancelledCalls);
        Assert.Equal(1, snapshot.Aggregate.Stages["ApplyFixes"].InterruptedCalls);
        Assert.Equal(0, snapshot.Aggregate.Stages["ApplyFixes"].ObservedDurationMilliseconds);
        Assert.Equal("complete", snapshot.Aggregate.Stages["ExecuteReasoning"].Usage!.InputTokens.Coverage);
        Assert.Null(snapshot.Aggregate.Stages["ApplyFixes"].Usage!.InputTokens.Tokens);
        var record = Assert.Single(snapshot.Calls);
        Assert.Equal(3, record.InputTokens);
        Assert.Equal(2, record.OutputTokens);
        Assert.Equal(5, record.TotalTokens);
        Assert.Equal(2000, record.DurationMilliseconds);
    }

    [Theory]
    [InlineData(ReviewForge.Core.Reasoning.ToolProfile.Review, ModelCallProfile.Review)]
    [InlineData(ReviewForge.Core.Reasoning.ToolProfile.Triage, ModelCallProfile.Triage)]
    [InlineData(ReviewForge.Core.Reasoning.ToolProfile.Fix, ModelCallProfile.Fix)]
    public async Task Agent_profiles_capture_physical_calls_but_not_task_done_guard_responses(
        ReviewForge.Core.Reasoning.ToolProfile profile, ModelCallProfile expected)
    {
        var raw = new ScriptedChatClient(ScriptedChatClient.FunctionCalls(("TaskDone",
            new Dictionary<string, object?> { [profile == ReviewForge.Core.Reasoning.ToolProfile.Triage ? "summary" : "reviewSummary"] = "secret-summary" })));
        var agent = new ReviewForge.Core.Reasoning.NativeReviewAgent(new FakeChatClientFactory(raw));
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning");
        await agent.RunAsync(new("secret prompt", new(), new(), Path.GetTempPath(), profile,
            AllowedThreadIds: profile == ReviewForge.Core.Reasoning.ToolProfile.Triage ? new HashSet<long>() : null,
            WritablePaths: profile == ReviewForge.Core.Reasoning.ToolProfile.Fix ? new HashSet<string> { "allowed.cs" } : null),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, raw.Calls);
        Assert.Equal(2, collector.Snapshot().Aggregate!.Activities!["attempt"].Observed);
        Assert.Equal(2, collector.Snapshot().Aggregate!.Activities!["logical"].Observed);
        Assert.Equal(1, collector.Snapshot().ObservedCalls);
        Assert.Equal(expected, Assert.Single(collector.Snapshot().Calls).Profile);
        var metadata = JsonSerializer.SerializeToElement(collector.Snapshot());
        Assert.True(metadata.TryGetProperty("Activities", out var activities), "T11 must capture tool/logical/attempt metadata");
        Assert.Contains(activities.EnumerateArray(), a => a.GetProperty("Kind").GetString() == "tool" && a.GetProperty("ToolName").GetString() == "TaskDone");
        var tool = activities.EnumerateArray().Single(a => a.GetProperty("Kind").GetString() == "tool");
        Assert.Equal(collector.Snapshot().Calls[0].CallId.ToString(), tool.GetProperty("ParentId").GetString());
        Assert.Equal((int)expected, tool.GetProperty("Profile").GetInt32());
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(collector.Snapshot()));
    }

    [Fact]
    public async Task Concurrent_scopes_on_shared_client_restore_parent_and_never_share_runs()
    {
        var raw = new ScriptedChatClient();
        var shared = new ProfiledChatClient(raw, "private provider", "private-model");
        var parent = new ModelProfileCollector(Guid.NewGuid());
        using var root = ModelCallContext.Enter(parent, "ExecuteReasoning");
        var collectors = Enumerable.Range(0, 20).Select(_ => new ModelProfileCollector(Guid.NewGuid())).ToArray();
        await Task.WhenAll(collectors.Select(async collector => {
            using var scope = ModelCallContext.Enter(collector, "VerifyFindings", ModelCallProfile.Verifier);
            await Task.Yield();
            await new ModelProfileChatClient(shared, ModelCallProfile.Fix).GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ModelCallProfile.Verifier, ModelCallContext.Current!.Profile);
        }));
        Assert.Same(parent, ModelCallContext.Current!.Collector);
        Assert.Empty(parent.Snapshot().Calls);
        foreach (var collector in collectors)
        {
            var call = Assert.Single(collector.Snapshot().Calls);
            Assert.Equal(collector.Snapshot().RunId, call.RunId);
            Assert.Equal(ModelCallProfile.Fix, call.Profile);
            Assert.Null(call.Model);
            Assert.Null(call.Provider);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failures_preserve_original_exception_with_safe_codes(bool cancelled)
    {
        Exception original = cancelled ? new OperationCanceledException("secret credentials") : new InvalidOperationException("secret source");
        var raw = new ScriptedChatClient { ThrowOnResponse = original };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "private/path", (ModelCallProfile)999);
        Assert.Same(original, await Record.ExceptionAsync(() => new ProfiledChatClient(raw, "openai", "gpt-5")
            .GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)));
        var call = Assert.Single(collector.Snapshot().Calls);
        Assert.Equal(cancelled ? "cancelled" : "failed", call.Outcome);
        Assert.Equal("unavailable", call.Stage);
        Assert.Equal(ModelCallProfile.Unspecified, call.Profile);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(call));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("interrupted")]
    public async Task Streaming_cumulative_usage_is_one_nullable_snapshot_and_disposal_is_honest(string outcome)
    {
        ChatResponseUpdate Update(long input, long? output) => new(ChatRole.Assistant, [new UsageContent(new UsageDetails {
            InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = 12,
            CachedInputTokenCount = 2, ReasoningTokenCount = -1, AdditionalCounts = new() { ["secret-source"] = 123 } })]);
        var raw = new ScriptedChatClient { StreamingUpdates = [Update(5, 2), Update(10, null)],
            ThrowOnStream = outcome == "failed" ? new InvalidOperationException("secret response") :
                outcome == "cancelled" ? new OperationCanceledException("secret credential") : null };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        var client = new ModelProfileChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), ModelCallProfile.Triage);
        using var scope = ModelCallContext.Enter(collector, "TriageComments");
        var observed = await Record.ExceptionAsync(async () => {
            await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken))
                if (outcome == "interrupted") break;
        });
        Assert.Equal(outcome is "failed" or "cancelled", observed is not null);
        var call = Assert.Single(collector.Snapshot().Calls);
        Assert.Equal(outcome, call.Outcome);
        Assert.Equal(outcome == "interrupted" ? 5 : 10, call.InputTokens);
        Assert.Equal(outcome == "interrupted" ? 2 : null, call.OutputTokens);
        Assert.Equal(2, call.CachedInputTokens);
        Assert.Null(call.ReasoningTokens);
        Assert.Equal(ModelCallProfile.Triage, call.Profile);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(call));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task Explicit_safe_provider_outcome_is_not_mislabeled_as_success(string outcome)
    {
        var response = ScriptedChatClient.Text("private error");
        response.AdditionalProperties = new() { ["reviewforge.profile.outcome"] = outcome };
        var update = new ChatResponseUpdate(ChatRole.Assistant, "private error") {
            AdditionalProperties = new() { ["reviewforge.profile.outcome"] = outcome } };
        var raw = new ScriptedChatClient(response) { StreamingUpdates = [update] };
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning");
        var client = new ProfiledChatClient(raw, "openai", "gpt-5");
        await client.GetResponseAsync([], cancellationToken: TestContext.Current.CancellationToken);
        await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
        Assert.All(collector.Snapshot().Calls, call => Assert.Equal(outcome, call.Outcome));
    }

    [Fact]
    public async Task Synchronous_stream_creation_failure_is_failed_not_interrupted()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid());
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning");
        var client = new ProfiledChatClient(new ScriptedChatClient(), "openai", "gpt-5");
        await Assert.ThrowsAsync<NotSupportedException>(async () => {
            await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal("failed", Assert.Single(collector.Snapshot().Calls).Outcome);
    }

    [Fact]
    public async Task Provider_boundary_collects_safe_nullable_metadata_and_caps_details()
    {
        var collector = new ModelProfileCollector(Guid.NewGuid(), 1);
        var response = ScriptedChatClient.Text("secret response");
        response.AdditionalProperties = new() { ["secret-tool-args"] = "secret credentials", ["cost"] = 123.45m };
        response.RawRepresentation = new { Source = "secret source", Token = "secret credentials" };
        var raw = new ScriptedChatClient(response, ScriptedChatClient.Text("secret response"));
        var client = new ProfiledChatClient(raw, "openai", "gpt-5");
        using (ModelCallContext.Enter(collector, "ReviewAgent", ModelCallProfile.Review))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "secret prompt")], cancellationToken: TestContext.Current.CancellationToken);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "secret source")], cancellationToken: TestContext.Current.CancellationToken);
        }
        var snapshot = collector.Snapshot();
        Assert.Equal(2, snapshot.ObservedCalls);
        Assert.Equal(2, snapshot.Aggregate!.CompletedCalls);
        Assert.True(snapshot.Truncated);
        var call = Assert.Single(snapshot.Calls);
        Assert.Equal("completed", call.Outcome);
        Assert.Null(call.InputTokens);
        Assert.Null(call.Cost);
        Assert.True(call.DurationMilliseconds >= 0);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(snapshot));
        Assert.Null(ModelCallContext.Current);
    }
}
