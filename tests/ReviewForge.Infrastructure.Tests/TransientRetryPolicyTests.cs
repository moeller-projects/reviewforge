using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class TransientRetryPolicyTests
{
    // Drives the fake clock forward until the policy's Task.Delay timers fire,
    // yielding real time between advances so continuations get scheduling slots.
    private static async Task<T> RunWithAdvanceAsync<T>(
        FakeTimeProvider clock, Task<T> task, TimeSpan step)
    {
        while (!task.IsCompleted)
        {
            clock.Advance(step);
            await Task.Delay(1);
        }

        return await task;
    }

    [Fact]
    public async Task Retries_transient_failure_then_succeeds()
    {
        var clock = new FakeTimeProvider();
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 5,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(30),
            },
            _ => true,
            logger: NullLogger.Instance,
            clock: clock);
        var attempts = 0;

        // `_` fails on the first three invocations (attempts 1-3) and succeeds on the fourth.
        var result = await RunWithAdvanceAsync(
            clock,
            policy.ExecuteAsync(
                _ => attempts++ < 3 ? Task.FromException<int>(new HttpRequestException("boom")) : Task.FromResult(42),
                "op",
                CancellationToken.None),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(42, result);
        Assert.Equal(4, attempts); // three transient failures, then success on the fourth attempt
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_rethrows()
    {
        var clock = new FakeTimeProvider();
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(150), // exercises computed-backoff clamp on attempt 2
            },
            _ => true,
            logger: NullLogger.Instance,
            clock: clock);
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => RunWithAdvanceAsync(
                clock,
                policy.ExecuteAsync(
                    _ =>
                    {
                        attempts++;
                        return Task.FromException<int>(new HttpRequestException("boom"));
                    },
                    "op",
                    CancellationToken.None),
                TimeSpan.FromMilliseconds(10)));

        Assert.Equal("boom", ex.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_exceptions()
    {
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromMilliseconds(10) },
            _ => false);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<int>(new InvalidOperationException("hard failure"));
                },
                "op",
                CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Stops_immediately_when_cancellation_was_requested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromMilliseconds(10) },
            _ => true);
        var attempts = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => policy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<int>(new OperationCanceledException(cts.Token));
                },
                "op",
                cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Backoff_doubles_between_attempts_with_jitter()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 5,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(30),
            },
            _ => true,
            clock: clock);
        var observed = new List<TimeSpan>();
        var attempts = 0;
        var fence = start;

        await RunWithAdvanceAsync(
            clock,
            policy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    var now = clock.GetUtcNow();
                    observed.Add(now - fence);
                    fence = now;
                    return attempts < 3 ? Task.FromException<int>(new HttpRequestException("t")) : Task.FromResult(1);
                },
                "op",
                CancellationToken.None),
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(TimeSpan.Zero, observed[0]);
        // FakeTimeProvider fires timers ~1 ms early, so lower bounds use 74 % of nominal;
        // upper slack absorbs the pump loop's scheduling lag.
        Assert.InRange(observed[1], TimeSpan.FromMilliseconds(74), TimeSpan.FromMilliseconds(160));
        Assert.InRange(observed[2], TimeSpan.FromMilliseconds(149), TimeSpan.FromMilliseconds(320));
        Assert.True(observed[2] > observed[1], "second wait should be longer than the first");
    }

    [Fact]
    public async Task Uses_server_retry_after_when_within_max()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(2),
            },
            _ => true,
            _ => TimeSpan.FromMilliseconds(300),
            clock: clock);
        var observed = new List<TimeSpan>();
        var attempts = 0;
        var fence = start;

        await RunWithAdvanceAsync(
            clock,
            policy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    var now = clock.GetUtcNow();
                    observed.Add(now - fence);
                    fence = now;
                    return attempts < 2 ? Task.FromException<int>(new HttpRequestException("t")) : Task.FromResult(1);
                },
                "op",
                CancellationToken.None),
            TimeSpan.FromMilliseconds(10));

        Assert.InRange(observed[1], TimeSpan.FromMilliseconds(225), TimeSpan.FromMilliseconds(375));
    }

    [Fact]
    public async Task Clamps_server_retry_after_to_max_delay()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromSeconds(2),
            },
            _ => true,
            _ => TimeSpan.FromDays(1),
            clock: clock);
        var observed = new List<TimeSpan>();
        var attempts = 0;
        var fence = start;

        await RunWithAdvanceAsync(
            clock,
            policy.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    var now = clock.GetUtcNow();
                    observed.Add(now - fence);
                    fence = now;
                    return attempts < 2 ? Task.FromException<int>(new HttpRequestException("t")) : Task.FromResult(1);
                },
                "op",
                CancellationToken.None),
            TimeSpan.FromMilliseconds(50));

        Assert.InRange(observed[1], TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(2500));
    }

    [Fact]
    public async Task Jitter_stays_within_band_and_varies()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var policy = new TransientRetryPolicy(
            new AdoRetryOptions
            {
                MaxAttempts = 40,
                BaseDelay = TimeSpan.FromMilliseconds(100),
                MaxDelay = TimeSpan.FromMilliseconds(100), // every computed delay clamps to 100 ms
            },
            _ => true,
            clock: clock);
        var observed = new List<TimeSpan>();
        var fence = start;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => RunWithAdvanceAsync(
                clock,
                policy.ExecuteAsync(
                    _ =>
                    {
                        var now = clock.GetUtcNow();
                        observed.Add(now - fence);
                        fence = now;
                        return Task.FromException<int>(new HttpRequestException("t"));
                    },
                    "op",
                    CancellationToken.None),
                TimeSpan.FromMilliseconds(5)));

        // Every wait must stay in the ±25 % band around the 100 ms nominal. The low
        // edge uses 74 ms because FakeTimeProvider fires timers ~1 ms early; the high
        // edge tolerates the pump loop's small scheduling lag.
        Assert.All(observed.Skip(1), delay =>
            Assert.InRange(delay, TimeSpan.FromMilliseconds(74), TimeSpan.FromMilliseconds(160)));
        Assert.True(observed.Skip(1).Distinct().Count() > 1, "jittered delays should not all be identical");
    }
}