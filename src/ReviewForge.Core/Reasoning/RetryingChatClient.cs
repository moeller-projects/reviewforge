using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Profiling;

namespace ReviewForge.Core.Reasoning;

/// <summary>Retries transient chat transport failures before a response is observed.</summary>
internal sealed class RetryingChatClient(IChatClient inner, Func<Exception, bool> isTransientFailure, ILogger? logger = null) : DelegatingChatClient(inner)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var collector = ModelCallContext.Current?.Collector;
        var logical = collector?.BeginActivity("logical");
        using var logicalScope = logical is null ? null : ModelCallContext.EnterCall(logical.Id);
        try
        {
            for (var number = 1;; number++)
            {
                var attempt = collector?.BeginActivity("attempt", attemptNumber: number);
                using var attemptScope = logical is null ? null : ModelCallContext.EnterCall(logical.Id, attempt!.Id, number, attempt);
                try
                {
                    var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                    attempt?.Complete("completed");
                    logical?.Complete("completed");
                    return response;
                }
                catch (Exception ex)
                {
                    attempt?.Complete(ex is OperationCanceledException ? "cancelled" : "failed");
                    if (!ShouldRetry(ex, number, cancellationToken)) throw;
                    await DelayAsync(number, ex, attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            logical?.Complete(ex is OperationCanceledException ? "cancelled" : "failed");
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages as ChatMessage[] ?? messages.ToArray();
        var collector = ModelCallContext.Current?.Collector;
        var logical = collector?.BeginActivity("logical");
        using var logicalScope = logical is null ? null : ModelCallContext.EnterCall(logical.Id);
        var logicalOutcome = "interrupted";
        try
        {
            for (var number = 1;; number++)
            {
                var attempt = collector?.BeginActivity("attempt", attemptNumber: number);
                using var attemptScope = logical is null ? null : ModelCallContext.EnterCall(logical.Id, attempt!.Id, number, attempt);
                IAsyncEnumerator<ChatResponseUpdate>? stream = null;
                var observedUpdate = false;
                Exception? failure = null;
                var outcome = "interrupted";
                try
                {
                    try { stream = base.GetStreamingResponseAsync(materialized, options, cancellationToken).GetAsyncEnumerator(cancellationToken); }
                    catch (Exception ex) { failure = ex; outcome = logicalOutcome = ex is OperationCanceledException ? "cancelled" : "failed"; throw; }
                    while (true)
                    {
                        bool next;
                        try { next = await stream.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            failure = ex;
                            outcome = ex is OperationCanceledException ? "cancelled" : "failed";
                            if (!observedUpdate && ShouldRetry(ex, number, cancellationToken)) break;
                            logicalOutcome = outcome;
                            throw;
                        }
                        if (!next) { outcome = logicalOutcome = "completed"; yield break; }
                        observedUpdate = true;
                        yield return stream.Current;
                    }
                }
                finally
                {
                    try { if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false); }
                    catch when (failure is not null) { /* Preserve the original transport failure, not a secondary disposal error. */ }
                    catch (Exception ex) { outcome = logicalOutcome = ex is OperationCanceledException ? "cancelled" : "failed"; throw; }
                    finally { attempt?.Complete(outcome); }
                }
                // Attempt failure is visible before the cancellable backoff begins.
                try { await DelayAsync(number, failure!, attempt, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { logicalOutcome = ex is OperationCanceledException ? "cancelled" : "failed"; throw; }
            }
        }
        finally { logical?.Complete(logicalOutcome); }
    }

    private bool ShouldRetry(Exception exception, int attempt, CancellationToken ct)
    {
        if (attempt >= MaxAttempts || ct.IsCancellationRequested || exception is OperationCanceledException) return false;
        return isTransientFailure(exception);
    }

    private async Task DelayAsync(int attempt, Exception exception, ModelProfileCollector.ExecutionActivityLease? activity, CancellationToken ct)
    {
        var delayMs = BaseDelay.TotalMilliseconds * (1 << (attempt - 1));
        var jittered = TimeSpan.FromMilliseconds(delayMs * (0.75 + Random.Shared.NextDouble() * 0.5));
        logger?.LogWarning(exception,
            "Chat request failed transiently (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs} ms",
            attempt, MaxAttempts, jittered.TotalMilliseconds);
        var started = Stopwatch.GetTimestamp();
        try { await Task.Delay(jittered, ct).ConfigureAwait(false); }
        finally { activity?.Wait(Stopwatch.GetElapsedTime(started).TotalMilliseconds, backoff: true); }
    }
}
