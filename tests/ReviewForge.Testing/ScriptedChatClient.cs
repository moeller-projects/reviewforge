using Microsoft.Extensions.AI;

namespace ReviewForge.Testing;

/// <summary>
/// Fake IChatClient replaying a script of responses. Each dequeued response is one
/// model turn; function-call responses drive the function-invoking loop. When the
/// script is exhausted, a plain "done" answer is returned.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly object _Gate = new();
    private readonly Queue<ChatResponse> _Script;

    public ScriptedChatClient(params ChatResponse[] script)
    {
        _Script = new Queue<ChatResponse>(script);
    }

    public int Calls { get; private set; }
    public Exception? ThrowOnResponse { get; set; }
    public Exception? ThrowOnStream { get; set; }
    public Exception? ThrowOnStreamDispose { get; set; }
    public IReadOnlyList<ChatResponseUpdate>? StreamingUpdates { get; set; }

    public List<IReadOnlyList<ChatMessage>> Received { get; } = [];

    public List<ChatOptions?> ReceivedOptions { get; } = [];



    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ThrowOnResponse is { } failure) throw failure;
        lock (_Gate)
        {
            Calls++;
            Received.Add([.. messages]);
            ReceivedOptions.Add(options);
            return Task.FromResult(_Script.Count > 0
                ? _Script.Dequeue()
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "script exhausted")));
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => StreamingUpdates is null ? throw new NotSupportedException("streaming not scripted")
            : ThrowOnStreamDispose is { } error ? new DisposalFailureStream(Stream(cancellationToken), error) : Stream(cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> Stream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var update in StreamingUpdates!)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return update;
        }
        if (ThrowOnStream is { } failure) throw failure;
    }

    private sealed class DisposalFailureStream(IAsyncEnumerable<ChatResponseUpdate> inner, Exception error) : IAsyncEnumerable<ChatResponseUpdate>
    {
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(inner.GetAsyncEnumerator(cancellationToken), error);
        private sealed class Enumerator(IAsyncEnumerator<ChatResponseUpdate> inner, Exception error) : IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => inner.Current;
            public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); throw error; }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <summary>Re-arms the script for a subsequent run (clears the queue and enqueues the new script).</summary>
    public void Reset(params ChatResponse[] script)
    {
        lock (_Gate)
        {
            _Script.Clear();
            foreach (var response in script)
            {
                _Script.Enqueue(response);
            }
        }
    }

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