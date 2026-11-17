using System.Threading.RateLimiting;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ReviewForge.Infrastructure.Chat;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class ConcurrencyLimitedChatClientTests
{
    [Fact]
    public async Task Streaming_response_holds_permit_until_enumeration_finishes()
    {
        await using var limiter = CreateLimiter(permitLimit: 1, queueLimit: 1);
        var inner = new GatedChatClient();
        using var client = new ConcurrencyLimitedChatClient(inner, limiter, TimeSpan.FromSeconds(5));
        await using var stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")])
            .GetAsyncEnumerator();

        var firstMove = stream.MoveNextAsync().AsTask();
        await inner.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedCall = client.GetResponseAsync([new ChatMessage(ChatRole.User, "queued")]);
        await WaitUntilAsync(() => limiter.GetStatistics()?.CurrentQueuedCount == 1);

        Assert.False(inner.ResponseStarted.Task.IsCompleted);

        inner.ReleaseStream.TrySetResult();
        Assert.True(await firstMove);
        Assert.False(await stream.MoveNextAsync());
        inner.ReleaseResponse.TrySetResult();
        var response = await queuedCall;

        Assert.Equal("complete", response.Text);
        Assert.Equal(1, inner.ResponseCalls);
    }

    [Fact]
    public async Task Queue_wait_expires_with_a_visible_timeout()
    {
        using var limiter = CreateLimiter(permitLimit: 1, queueLimit: 1);
        var inner = new GatedChatClient();
        using var client = new ConcurrencyLimitedChatClient(inner, limiter, TimeSpan.FromMilliseconds(20));

        var first = client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        await inner.ResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "queued")]));

        inner.ReleaseResponse.TrySetResult();
        await first;
        Assert.Equal(1, inner.ResponseCalls);
    }

    [Fact]
    public async Task Queue_limit_rejects_excess_waiters()
    {
        using var limiter = CreateLimiter(permitLimit: 1, queueLimit: 1);
        var inner = new GatedChatClient();
        using var client = new ConcurrencyLimitedChatClient(inner, limiter, TimeSpan.FromSeconds(5));

        var first = client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        await inner.ResponseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]);
        await WaitUntilAsync(() => limiter.GetStatistics()?.CurrentQueuedCount == 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "third")]));

        inner.ReleaseResponse.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, inner.ResponseCalls);
    }

    private static ConcurrencyLimiter CreateLimiter(int permitLimit, int queueLimit)
        => new(new ConcurrencyLimiterOptions
        {
            PermitLimit = permitLimit,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class GatedChatClient : IChatClient
    {
        public TaskCompletionSource StreamStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResponseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ResponseCalls { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ResponseCalls++;
            ResponseStarted.TrySetResult();
            await ReleaseResponse.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "complete"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamStarted.TrySetResult();
            await ReleaseStream.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
