using System.Collections.Concurrent;

namespace ReviewForge.Infrastructure.Git;

/// <summary>
/// Executes synchronous LibGit2Sharp work on a small set of dedicated background
/// threads so long-running clones/fetches never pin thread-pool threads. Cancellation
/// is honored before a work item starts; a running LibGit2Sharp call cannot be aborted
/// (library limitation) and completes in the background while the caller stops awaiting.
/// </summary>
public sealed class GitOperationScheduler : IDisposable
{
    private sealed record WorkItem(
        Func<object?> Work,
        TaskCompletionSource<object?> Completion,
        CancellationToken Ct,
        CancellationTokenRegistration Registration);

    private readonly BlockingCollection<WorkItem> _Queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread[] _Threads;
    private int _Disposed;

    public GitOperationScheduler(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _Threads = new Thread[maxConcurrency];
        for (var i = 0; i < _Threads.Length; i++)
        {
            _Threads[i] = new Thread(Drain)
            {
                IsBackground = true,
                Name = $"reviewforge-git-{i}",
            };
            _Threads[i].Start();
        }
    }

    public int MaxConcurrency => _Threads.Length;

    public Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_Disposed != 0, this);
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A pre-cancelled token fires the registration synchronously and then faults the
        // enqueue; both paths funnel through the cancellation handling below.
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        try
        {
            _Queue.Add(new WorkItem(() =>
            {
                ct.ThrowIfCancellationRequested();
                return work();
            }, completion, ct, registration), ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Cancelled before enqueue, or scheduler is completing.
            registration.Dispose();
            completion.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
        }

        return Unwrap<T>(completion);
    }

    private static async Task<T> Unwrap<T>(TaskCompletionSource<object?> completion)
        => (T)(await completion.Task.ConfigureAwait(false))!;

    private void Drain()
    {
        try
        {
            foreach (var item in _Queue.GetConsumingEnumerable())
            {
                try
                {
                    item.Completion.TrySetResult(item.Work());
                }
                catch (OperationCanceledException) when (item.Ct.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.Ct);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    item.Registration.Dispose();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Scheduler disposed while draining; the background thread exits.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _Disposed, 1) != 0)
        {
            return;
        }

        _Queue.CompleteAdding();
        // Threads are background; in-flight LibGit2Sharp calls finish on their own.
        _Queue.Dispose();
    }
}
