using Microsoft.Extensions.AI;

namespace ReviewForge.Core.Reasoning;

/// <summary>Stops the function-invoking chat loop after task_done completes the run.</summary>
internal static class TaskDoneChatClientMiddleware
{
    private const string DoneText = "Review already marked as done.";

    public static IChatClient Create(ReviewCollector collector, IChatClient inner)
        => new ChatClientBuilder(inner)
            .Use(
                getResponseFunc: (messages, options, client, ct) => collector.Done
                    ? Task.FromResult(CreateDoneResponse())
                    : client.GetResponseAsync(messages, options, ct),
                getStreamingResponseFunc: (messages, options, client, ct) => collector.Done
                    ? CreateDoneResponse().ToChatResponseUpdates().ToAsyncEnumerable()
                    : client.GetStreamingResponseAsync(messages, options, ct))
            .Build();

    private static ChatResponse CreateDoneResponse()
        => new(new ChatMessage(ChatRole.Assistant, DoneText));
}
