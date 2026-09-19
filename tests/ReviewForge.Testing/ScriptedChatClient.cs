using Microsoft.Extensions.AI;

namespace ReviewForge.Testing;

/// <summary>
/// Fake IChatClient replaying a script of responses. Each dequeued response is one
/// model turn; function-call responses drive the function-invoking loop. When the
/// script is exhausted, a plain "done" answer is returned.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _Script;

    public ScriptedChatClient(params ChatResponse[] script) => _Script = new Queue<ChatResponse>(script);

    public int Calls { get; private set; }

    public List<IReadOnlyList<ChatMessage>> Received { get; } = [];

    public List<ChatOptions?> ReceivedOptions { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Received.Add([.. messages]);
        ReceivedOptions.Add(options);
        return Task.FromResult(_Script.Count > 0
            ? _Script.Dequeue()
            : new ChatResponse(new ChatMessage(ChatRole.Assistant, "script exhausted")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("streaming not scripted");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>One assistant turn consisting of function calls.</summary>
    public static ChatResponse FunctionCalls(params (string Name, IDictionary<string, object?> Args)[] calls)
        => new(new ChatMessage(ChatRole.Assistant,
            [.. calls.Select((c, i) => new FunctionCallContent($"call-{i}-{c.Name}", c.Name, c.Args))]));

    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));
}