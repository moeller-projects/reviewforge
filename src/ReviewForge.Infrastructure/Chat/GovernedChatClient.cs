using System.Diagnostics;
using Microsoft.Extensions.AI;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Infrastructure.Chat;

/// <summary>A slot-acquisition timeout — a normal, visible run failure (no engine
/// fallback); the run is recorded as failed and the reaper/backoff covers the retry.</summary>
public sealed class LlmGovernorTimeoutException(string message) : TimeoutException(message);

/// <summary>
/// Admission control around each provider request: acquires a <see cref="LlmGovernor"/>
/// slot with a bounded wait before delegating. Sits between usage tracking and the
/// transport so tracking measures real provider traffic while the governor shapes it.
/// </summary>
public sealed class GovernedChatClient(
    IChatClient inner,
    LlmGovernor governor,
    int acquireTimeoutSeconds = 300,
    TimeProvider? clock = null) : DelegatingChatClient(inner)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var slot = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // The slot is held for the whole enumeration — a streamed response is one
        // in-flight provider request until the terminal update.
        using var slot = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(acquireTimeoutSeconds), _Clock);
            var slot = await governor.AcquireAsync(timeoutCts.Token).ConfigureAwait(false);
            ReviewForgeTelemetry.LlmGovernorWait.Record(sw.Elapsed.TotalMilliseconds);
            return slot;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ReviewForgeTelemetry.LlmGovernorTimeouts.Add(1);
            throw new LlmGovernorTimeoutException(
                $"timed out after {acquireTimeoutSeconds}s waiting for an LLM concurrency slot " +
                $"({governor.Inflight} in flight, cap {governor.MaxConcurrency})");
        }
    }
}
