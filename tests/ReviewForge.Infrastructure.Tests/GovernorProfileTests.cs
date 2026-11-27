using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Profiling;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class GovernorProfileTests
{
    [Theory]
    [InlineData(false, "completed")]
    [InlineData(true, "completed")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    [InlineData(false, "timeout")]
    [InlineData(true, "timeout")]
    public async Task Admission_wait_is_separate_from_provider_latency_and_failed_admission_is_not_a_model_call(bool streaming, string outcome)
    {
        using var governor = new LlmGovernor(1);
        var blocker = await governor.AcquireAsync(TestContext.Current.CancellationToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new FakeTimeProvider();
        var collector = new ModelProfileCollector(Guid.NewGuid(), clock: clock);
        using var scope = ModelCallContext.Enter(collector, "ExecuteReasoning", ModelCallProfile.Review);
        var logical = collector.BeginActivity("logical");
        using var logicalScope = ModelCallContext.EnterCall(logical.Id);
        var attempt = collector.BeginActivity("attempt", attemptNumber: 1);
        using var attemptScope = ModelCallContext.EnterCall(logical.Id, attempt.Id, 1, attempt);
        var raw = new ScriptedChatClient { StreamingUpdates = [] };
        var client = new GovernedChatClient(new ProfiledChatClient(raw, "openai", "gpt-5"), governor, outcome == "timeout" ? 0 : 30);
        var pending = Record.ExceptionAsync(async () => {
            if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: cancelled.Token)) { }
            else await client.GetResponseAsync([], cancellationToken: cancelled.Token);
        });
        Assert.Empty(collector.Snapshot().Calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        if (outcome == "cancelled") cancelled.Cancel();
        if (outcome == "completed") blocker.Dispose();
        var error = await pending;
        if (outcome == "timeout") Assert.IsType<LlmGovernorTimeoutException>(error);
        else if (outcome == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Null(error);
        blocker.Dispose();
        attempt.Complete(outcome == "timeout" ? "failed" : outcome);
        logical.Complete(outcome == "timeout" ? "failed" : outcome);
        var snapshot = collector.Snapshot();
        Assert.True(snapshot.Activities!.Single(a => a.Kind == "attempt").GovernorWaitMilliseconds >= 0);
        Assert.Null(snapshot.Activities!.Single(a => a.Kind == "logical").GovernorWaitMilliseconds);
        Assert.Equal(outcome == "completed" ? 1 : 0, snapshot.ObservedCalls);
        if (outcome == "completed") Assert.Equal(0, Assert.Single(snapshot.Calls).DurationMilliseconds);
        Assert.Equal(0, governor.Inflight);
    }
}
