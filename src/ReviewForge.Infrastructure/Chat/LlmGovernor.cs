namespace ReviewForge.Infrastructure.Chat;

/// <summary>
/// Process-wide admission control for provider HTTP requests. Every chat transport
/// (one per model tier) draws from this single pool, so WorkerCount × iterations (and
/// later × shards) cannot burst the provider into 429s. Slots are acquired around each
/// HTTP call, not each run — the function-invocation loop issues one call per iteration.
/// </summary>
public sealed class LlmGovernor
{
    private readonly SemaphoreSlim _Slots;
    private readonly int _Max;

    public LlmGovernor(int maxConcurrentRequests)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentRequests, 1);
        _Max = maxConcurrentRequests;
        _Slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    public int MaxConcurrency => _Max;

    /// <summary>Slots currently held by in-flight provider requests.</summary>
    public int Inflight => _Max - _Slots.CurrentCount;

    /// <summary>Acquires one request slot; disposing the returned handle releases it.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _Slots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_Slots);
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _Released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
