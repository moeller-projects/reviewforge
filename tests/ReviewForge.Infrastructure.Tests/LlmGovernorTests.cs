using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class LlmGovernorTests
{
    /// <summary>Records the governor's observed inflight count at call start; optionally
    /// blocks on a gate and always yields so concurrent calls genuinely overlap.</summary>
    private sealed class GateChatClient(
        Func<int> inflight,
        Task? gate = null,
        int delayMs = 0) : IChatClient
    {
        public int MaxObservedInflight { get; private set; }

        private async Task EnterAsync()
        {
            MaxObservedInflight = Math.Max(MaxObservedInflight, inflight());
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            if (gate is { } held)
            {
                await held;
            }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await EnterAsync();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await EnterAsync();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Constructor_rejects_non_positive_cap()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new LlmGovernor(0));

    [Fact]
    public async Task Concurrency_cap_is_honored_under_parallel_load()
    {
        var governor = new LlmGovernor(2);
        var inner = new GateChatClient(() => governor.Inflight, delayMs: 25);
        var governed = new GovernedChatClient(inner, governor, acquireTimeoutSeconds: 30);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            governed.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])));

        Assert.Equal(2, inner.MaxObservedInflight);
        Assert.Equal(0, governor.Inflight); // all slots released
    }

    [Fact]
    public async Task Acquire_timeout_throws_the_distinct_exception_and_counts_it()
    {
        var governor = new LlmGovernor(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuedAsynchronously);
        var holder = new GateChatClient(() => governor.Inflight, release.Task);
        var governed = new GovernedChatClient(holder, governor, acquireTimeoutSeconds: 1);

        var held = governed.GetResponseAsync([new ChatMessage(ChatRole.User, "hold the slot")]);
        while (governor.Inflight == 0)
        {
            await Task.Delay(10);
        }

        long timeouts = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.llm.governor.timeout_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            Interlocked.Add(ref timeouts, measurement));
        listener.Start();

        var ex = await Assert.ThrowsAsync<LlmGovernorTimeoutException>(() =>
            governed.GetResponseAsync([new ChatMessage(ChatRole.User, "queue behind the holder")]));

        Assert.Contains("waiting for an LLM concurrency slot", ex.Message);
        Assert.Equal(1, Interlocked.Read(ref timeouts));

        release.TrySetResult();
        await held;
        Assert.Equal(0, governor.Inflight);
    }

    [Fact]
    public async Task Wait_time_is_recorded_for_a_queued_request()
    {
        var governor = new LlmGovernor(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuedAsynchronously);
        var inner = new GateChatClient(() => governor.Inflight, release.Task);
        var governed = new GovernedChatClient(inner, governor, acquireTimeoutSeconds: 30);

        var held = governed.GetResponseAsync([new ChatMessage(ChatRole.User, "hold")]);
        while (governor.Inflight == 0)
        {
            await Task.Delay(10);
        }

        var waits = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.llm.governor.wait_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) => Interlocked.Increment(ref waits));
        listener.Start();

        var waited = governed.GetResponseAsync([new ChatMessage(ChatRole.User, "wait behind the holder")]);
        await waited;
        release.TrySetResult();
        await held;

        Assert.True(waits >= 1, "the queued request's slot acquisition was not recorded");
        Assert.Equal(0, governor.Inflight);
    }

    [Fact]
    public async Task Streaming_holds_the_slot_for_the_whole_enumeration()
    {
        var governor = new LlmGovernor(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuedAsynchronously);
        var inner = new GateChatClient(() => governor.Inflight, gate.Task);
        var governed = new GovernedChatClient(inner, governor, acquireTimeoutSeconds: 30);

        var enumerator = governed
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")])
            .GetAsyncEnumerator();

        var move = enumerator.MoveNextAsync().AsTask();
        while (governor.Inflight == 0)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, governor.Inflight); // slot held while the provider streams
        gate.TrySetResult();
        Assert.True(await move);
        await enumerator.DisposeAsync();
        Assert.Equal(0, governor.Inflight); // released when enumeration completes
    }

    [Fact]
    public void Factory_wraps_clients_with_the_governor_when_provided()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var governor = new LlmGovernor(4);
            var factory = new ChatClientFactory(
                new ChatProviderOptions {Provider = "openai", Model = "gpt-5"},
                governor: governor);

            Assert.IsType<GovernedChatClient>(factory.Create(ChatTier.Full));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public void Factory_without_governor_builds_unwrapped_clients()
    {
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var factory = new ChatClientFactory(new ChatProviderOptions {Provider = "openai", Model = "gpt-5"});

            Assert.IsNotType<GovernedChatClient>(factory.Create(ChatTier.Full));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }
}
