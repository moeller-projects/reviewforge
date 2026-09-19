using Microsoft.Extensions.AI;

namespace ReviewForge.Infrastructure.Codex;

/// <summary>
/// Adapts the Codex Responses API's required SSE transport to callers that request
/// a complete response. The agent framework uses GetResponseAsync, while Codex
/// requires stream=true and therefore must be consumed through GetStreamingResponseAsync.
/// </summary>
public sealed class CodexStreamingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
}