namespace ReviewForge.Service;

/// <summary>
/// Periodic org-wide sweep. Disabled when no SweepInterval is configured; a failing sweep is
/// logged and never crashes the host.
/// </summary>
public sealed class DiscoverySweepWorker(
    DiscoveryService discovery,
    DiscoveryOptions options,
    ILogger<DiscoverySweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.SweepInterval is not { } interval)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await discovery.RunSweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "discovery sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }
}