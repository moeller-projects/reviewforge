using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class CodexStreamingChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_aggregates_the_inner_stream()
    {
        var client = new CodexStreamingChatClient(new FakeStreamingClient());

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("hello", response.Text);
    }

    private sealed class FakeStreamingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Codex requires the streaming transport");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "hel");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "lo");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}