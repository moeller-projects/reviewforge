using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Termination guard: once the agent called task_done, any further model call is
/// short-circuited with a fixed assistant reply — the function-invoking loop sees no
/// tool requests and exits, instead of burning tokens after completion.
/// The reply must be a FRESH response per call: the function invoker aggregates loop
/// usage into the final response's message contents, so a shared static response would
/// carry (and keep growing) every prior run's usage — polluting token metrics.
/// </summary>
public sealed class TaskDoneGuardChatClient(ReviewCollector collector, IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    private const string DoneText = "Review already marked as done.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => collector.Done
            ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, DoneText)))
            : base.GetResponseAsync(messages, options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => collector.Done
            ? new ChatResponse(new ChatMessage(ChatRole.Assistant, DoneText)).ToChatResponseUpdates().ToAsyncEnumerable()
            : base.GetStreamingResponseAsync(messages, options, cancellationToken);
}