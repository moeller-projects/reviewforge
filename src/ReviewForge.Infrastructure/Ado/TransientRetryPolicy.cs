using Microsoft.Extensions.Logging;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>Knobs for transient-failure retries against Azure DevOps.</summary>
public sealed record AdoRetryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Transport-agnostic jittered exponential backoff with an optional server-provided
/// (Retry-After) delay override. Pure policy — unit-tested; the ADO adapter supplies
/// the exception classification.
/// </summary>
public sealed class TransientRetryPolicy(
    AdoRetryOptions options,
    Func<Exception, bool> isTransient,
    Func<Exception, TimeSpan?>? retryAfterProbe = null,
    ILogger? logger = null,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken ct)
    {
        var attempt = 0;
        var delay = options.BaseDelay;
        while (true)
        {
            attempt++;
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < options.MaxAttempts
                                       && !ct.IsCancellationRequested
                                       && isTransient(ex))
            {
                var wait = retryAfterProbe?.Invoke(ex) is { } serverDelay
                    ? Clamp(serverDelay)
                    : Clamp(delay);
                wait = Jitter(wait);

                logger?.LogWarning(ex,
                    "ADO call {Operation} failed transiently (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs} ms",
                    operationName, attempt, options.MaxAttempts, wait.TotalMilliseconds);

                await Task.Delay(wait, _Clock, ct).ConfigureAwait(false);
                delay += delay;
            }
        }
    }

    private TimeSpan Clamp(TimeSpan value)
        => value > options.MaxDelay ? options.MaxDelay : value;

    private static TimeSpan Jitter(TimeSpan value)
    {
        var factor = 0.75 + Random.Shared.NextDouble() * 0.5; // ±25 %
        return TimeSpan.FromTicks((long)(value.Ticks * factor));
    }
}
