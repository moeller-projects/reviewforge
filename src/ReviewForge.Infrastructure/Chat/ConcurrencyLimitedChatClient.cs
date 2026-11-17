using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Microsoft.Extensions.AI;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Holds one provider permit for the full chat operation, including streamed response-body
/// consumption. The standard HTTP bulkhead releases its permit after response headers, so it
/// cannot enforce ReviewForge's in-flight inference cap for long-lived streams.
/// </summary>
internal sealed class ConcurrencyLimitedChatClient : DelegatingChatClient
{
    private readonly RateLimiter _Limiter;
    private readonly TimeSpan _AcquireTimeout;

    public ConcurrencyLimitedChatClient(IChatClient inner, RateLimiter limiter, TimeSpan acquireTimeout)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(acquireTimeout, TimeSpan.Zero);
        _Limiter = limiter;
        _AcquireTimeout = acquireTimeout;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_AcquireTimeout);
        try
        {
            var lease = await _Limiter.AcquireAsync(1, timeout.Token).ConfigureAwait(false);
            if (lease.IsAcquired)
            {
                return lease;
            }

            lease.Dispose();
            throw new InvalidOperationException("The provider request queue is full.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {_AcquireTimeout.TotalSeconds:0} seconds waiting for a provider concurrency permit.");
        }
    }
}
