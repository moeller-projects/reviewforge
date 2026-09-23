using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Termination guard: once the agent called task_done, any further model call is
/// short-circuited with a fixed assistant reply — the function-invoking loop sees no
/// tool requests and exits, instead of burning tokens after completion.
/// </summary>
public sealed class TaskDoneGuardChatClient(ReviewCollector collector, IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    private static readonly ChatResponse DoneResponse =
        new(new ChatMessage(ChatRole.Assistant, "Review already marked as done."));

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => collector.Done
            ? Task.FromResult(DoneResponse)
            : base.GetResponseAsync(messages, options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => collector.Done
            ? DoneResponse.ToChatResponseUpdates().ToAsyncEnumerable()
            : base.GetStreamingResponseAsync(messages, options, cancellationToken);
}