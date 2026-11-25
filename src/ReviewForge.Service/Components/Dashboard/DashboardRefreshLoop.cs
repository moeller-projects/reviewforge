namespace ReviewForge.Service.Components.Dashboard;

/// <summary>One page-owned schedule. Calls during a load share one trailing refresh, never concurrent HTTP requests.</summary>
public sealed class DashboardRefreshLoop(TimeProvider clock, Func<bool> isActive, Func<CancellationToken, Task> load) : IAsyncDisposable
{
    private readonly CancellationTokenSource _Lifetime = new();
    private Task _Loop = Task.CompletedTask;
    private Task _Load = Task.CompletedTask;
    private bool _Pending;
    private bool _Stopped;
    private bool _Started;
    private CancellationTokenSource? _Delay;

    // All entry points are invoked on the component's dispatcher.
    public void Start()
    {
        if (_Started || _Stopped) return;
        _Started = true;
        _Loop = RunAsync();
    }

    public Task RequestAsync()
    {
        if (_Stopped) return Task.CompletedTask;
        _Pending = true;
        if (_Load.IsCompleted) _Load = DrainAsync();
        _Delay?.Cancel();
        return _Load;
    }

    private async Task DrainAsync()
    {
        try
        {
            while (_Pending && !_Stopped)
            {
                _Pending = false;
                await load(_Lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_Lifetime.IsCancellationRequested) { }
    }

    private async Task RunAsync()
    {
        try
        {
            await RequestAsync();
            while (!_Stopped)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(_Lifetime.Token);
                _Delay = delay;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(isActive() ? 3 : 25), clock, delay.Token);
                }
                catch (OperationCanceledException) when (!_Lifetime.IsCancellationRequested)
                {
                    _Delay = null;
                    await _Load;
                    continue; // Manual/accepted refresh resets cadence from its completed snapshot.
                }
                _Delay = null;
                await RequestAsync();
            }
        }
        catch (OperationCanceledException) when (_Lifetime.IsCancellationRequested) { }
    }

    public void Stop()
    {
        if (_Stopped) return;
        _Stopped = true;
        _Lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Task.WhenAll(_Loop, _Load);
        _Lifetime.Dispose();
    }
}
