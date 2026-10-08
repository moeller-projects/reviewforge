using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ReviewForge.Core.Reasoning;

/// <summary>Retries transient chat transport failures before a response is observed.</summary>
internal sealed class RetryingChatClient(IChatClient inner, ILogger? logger = null) : DelegatingChatClient(inner)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(token => base.GetResponseAsync(messages, options, token), cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as ChatMessage[] ?? messages.ToArray();
        for (var attempt = 1;; attempt++)
        {
            var stream = base.GetStreamingResponseAsync(materialized, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            var observedUpdate = false;
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        yield break;
                    }

                    update = stream.Current;
                }
                catch (Exception ex) when (!observedUpdate && ShouldRetry(ex, attempt, cancellationToken))
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    await DelayAsync(attempt, ex, cancellationToken).ConfigureAwait(false);
                    break;
                }

                observedUpdate = true;
                yield return update;
            }
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt, ct))
            {
                await DelayAsync(attempt, ex, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldRetry(Exception exception, int attempt, CancellationToken ct)
    {
        if (attempt >= MaxAttempts || ct.IsCancellationRequested || exception is OperationCanceledException)
            return false;

        return exception switch
        {
            HttpRequestException {StatusCode: null} => true,
            HttpRequestException {StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests} => true,
            HttpRequestException {StatusCode: { } status} when (int) status >= 500 => true,
            TimeoutException => true,
            _ => false,
        };
    }

    private async Task DelayAsync(int attempt, Exception exception, CancellationToken ct)
    {
        var delayMs = BaseDelay.TotalMilliseconds * (1 << (attempt - 1));
        var jittered = TimeSpan.FromMilliseconds(delayMs * (0.75 + Random.Shared.NextDouble() * 0.5));
        logger?.LogWarning(exception,
            "Chat request failed transiently (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs} ms",
            attempt, MaxAttempts, jittered.TotalMilliseconds);
        await Task.Delay(jittered, ct).ConfigureAwait(false);
    }
}