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
    private sealed class WorkItem(Func<object?> work, TaskCompletionSource<object?> completion, CancellationToken ct)
    {
        private int _State;

        public Func<object?> Work { get; } = work;
        public TaskCompletionSource<object?> Completion { get; } = completion;
        public CancellationToken Ct { get; } = ct;

        public bool TryStart() => Interlocked.CompareExchange(ref _State, 1, 0) == 0;

        public void CancelIfQueued()
        {
            if (Interlocked.CompareExchange(ref _State, 2, 0) == 0)
            {
                Completion.TrySetCanceled(Ct);
            }
        }
    }

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
        var item = new WorkItem(() =>
        {
            ct.ThrowIfCancellationRequested();
            return work();
        }, completion, ct);
        var cancellationRegistration = ct.CanBeCanceled
            ? ct.UnsafeRegister(static state => ((WorkItem)state!).CancelIfQueued(), item)
            : default;

        try
        {
            _Queue.Add(item, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Cancelled before enqueue, or scheduler is completing.
            item.CancelIfQueued();
        }

        return Unwrap<T>(completion, cancellationRegistration);
    }

    private static async Task<T> Unwrap<T>(
        TaskCompletionSource<object?> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return (T)(await completion.Task.ConfigureAwait(false))!;
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    private void Drain()
    {
        try
        {
            foreach (var item in _Queue.GetConsumingEnumerable())
            {
                if (!item.TryStart())
                {
                    continue;
                }
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
        foreach (var thread in _Threads)
        {
            thread.Join();
        }
        _Queue.Dispose();
    }
}
