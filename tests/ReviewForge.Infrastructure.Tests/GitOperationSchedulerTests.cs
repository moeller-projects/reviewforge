using ReviewForge.Infrastructure.Git;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class GitOperationSchedulerTests
{
    [Fact]
    public async Task RunAsync_executes_work_and_returns_result()
    {
        using var scheduler = new GitOperationScheduler(2);
        Assert.Equal(2, scheduler.MaxConcurrency);
        var result = await scheduler.RunAsync(() => 42, CancellationToken.None);
        Assert.Equal(42, result);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var scheduler = new GitOperationScheduler(1);
        scheduler.Dispose();
        scheduler.Dispose(); // second dispose is a no-op
    }

    [Fact]
    public async Task RunAsync_bounds_concurrency()
    {
        using var scheduler = new GitOperationScheduler(2);
        var active = 0;
        var maxActive = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => scheduler.RunAsync(() =>
            {
                var current = Interlocked.Increment(ref active);
                while (true)
                {
                    var observed = Volatile.Read(ref maxActive);
                    if (current <= observed || Interlocked.CompareExchange(ref maxActive, current, observed) == observed)
                    {
                        break;
                    }
                }

                if (current >= 2)
                {
                    entered.TrySetResult();
                }

                release.Wait(TimeSpan.FromSeconds(10));
                Interlocked.Decrement(ref active);
                return true;
            }, CancellationToken.None))
            .ToArray();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50); // let any (would-be) third item attempt to start
        Assert.Equal(2, Volatile.Read(ref maxActive));

        release.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(2, Volatile.Read(ref maxActive)); // never exceeded the bound
    }

    [Fact]
    public async Task RunAsync_pre_cancelled_token_cancels_without_running()
    {
        using var scheduler = new GitOperationScheduler(1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.RunAsync(() =>
            {
                ran = true;
                return true;
            }, cts.Token));

        Assert.False(ran);
    }

    [Fact]
    public async Task RunAsync_cancellation_while_queued_cancels()
    {
        using var scheduler = new GitOperationScheduler(1);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blocking = scheduler.RunAsync(() =>
        {
            started.Set();
            release.Wait();
            return true;
        }, CancellationToken.None);

        started.Wait(); // occupy the single thread

        using var cts = new CancellationTokenSource();
        var ran = false;
        var queued = scheduler.RunAsync(() =>
        {
            ran = true;
            return true;
        }, cts.Token);

        cts.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(ran);
        await blocking;
    }

    [Fact]
    public async Task RunAsync_cancellation_of_running_item_waits_for_native_work()
    {
        using var scheduler = new GitOperationScheduler(1);
        using var started = new ManualResetEventSlim(false);
        using var finish = new ManualResetEventSlim(false);
        var sideEffect = false;

        using var cts = new CancellationTokenSource();
        var task = scheduler.RunAsync(() =>
        {
            started.Set();
            finish.Wait();
            sideEffect = true;
            return true;
        }, cts.Token);

        started.Wait();
        cts.Cancel();

        Assert.False(task.IsCompleted);
        finish.Set();

        Assert.True(await task);
        Assert.True(sideEffect);
    }

    [Fact]
    public async Task RunAsync_propagates_exceptions()
    {
        using var scheduler = new GitOperationScheduler(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.RunAsync<bool>(() => throw new InvalidOperationException("boom"), CancellationToken.None));

        // The scheduler keeps draining after a fault.
        Assert.Equal(42, await scheduler.RunAsync(() => 42, CancellationToken.None));
    }

    [Fact]
    public void Dispose_rejects_new_work()
    {
        var scheduler = new GitOperationScheduler(1);
        scheduler.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { _ = scheduler.RunAsync(() => true, CancellationToken.None); });
    }
}
