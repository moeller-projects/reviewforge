using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public sealed record CachedInventory(PullRequestInventory Inventory, DateTimeOffset FetchedAt)
{
    public bool IsStale { get; init; }
    public bool Refreshing { get; init; }
    public bool RefreshFailed { get; init; }
}

/// <summary>Demand-driven single-flight stale-while-revalidate metadata cache. Only cold loads wait.
/// Fetches have a localized two-minute budget (organization-wide paging can be slow), independent of
/// individual waiters, and are cancelled on host stop/disposal. No review/discovery effects.</summary>
public sealed class DashboardInventoryCache : IDisposable
{
    private readonly IPullRequestSource _Source;
    private readonly TimeProvider _Clock;
    private readonly object _Gate = new();
    private readonly CancellationTokenSource _Stop = new();
    private readonly CancellationTokenRegistration _Stopping;
    private Task<CachedInventory>? _Fetch;
    private CachedInventory? _Last;
    private bool _Failed;
    private DateTimeOffset _RetryAfter;
    private bool _Disposed;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    public DashboardInventoryCache(IPullRequestSource source, TimeProvider clock, IHostApplicationLifetime? lifetime = null)
    {
        _Source = source;
        _Clock = clock;
        _Stopping = lifetime?.ApplicationStopping.Register(() => _Stop.Cancel()) ?? default;
    }

    public Task<CachedInventory> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_Gate)
        {
            ObjectDisposedException.ThrowIf(_Disposed, this);
            var stale = _Last is not null && _Clock.GetUtcNow() - _Last.FetchedAt >= Ttl;
            if ((_Fetch is null || _Fetch.IsCompleted) && (_Last is null || (stale && _Clock.GetUtcNow() >= _RetryAfter)))
            {
                _Fetch = FetchAsync();
                // Background refresh faults are surfaced as safe markers; observe their exceptions too.
                _ = _Fetch.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            stale = _Last is not null && _Clock.GetUtcNow() - _Last.FetchedAt >= Ttl;
            if (_Last is not null)
                return Task.FromResult(stale ? _Last with { IsStale = true, Refreshing = !_Fetch!.IsCompleted, RefreshFailed = _Failed } : _Last);
            return _Fetch!.WaitAsync(ct);
        }
    }

    private async Task<CachedInventory> FetchAsync()
    {
        using var timeout = new CancellationTokenSource(FetchTimeout, _Clock);
        using var fetch = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _Stop.Token);
        try
        {
            // WaitAsync bounds even an adapter that fails to honor cancellation.
            var inventory = await _Source.GetOpenPullRequestInventoryAsync(fetch.Token).WaitAsync(fetch.Token).ConfigureAwait(false);
            lock (_Gate)
            {
                _Last = new(inventory, _Clock.GetUtcNow());
                _Failed = false;
                return _Last;
            }
        }
        catch
        {
            lock (_Gate)
            {
                _Failed = true;
                _RetryAfter = _Clock.GetUtcNow() + Ttl;
            }
            throw;
        }
    }

    public void Dispose()
    {
        lock (_Gate)
        {
            if (_Disposed) return;
            _Disposed = true;
        }
        _Stopping.Dispose();
        _Stop.Cancel();
        _Stop.Dispose();
    }
}
