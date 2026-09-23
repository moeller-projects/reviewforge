using Microsoft.Extensions.Options;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

/// <summary>Periodic bounded cleanup of idle per-head repository checkouts.</summary>
public sealed class CheckoutEvictionWorker(
    RepoCheckoutPool pool,
    IOptions<ReviewForgeServiceOptions> options,
    TimeProvider clock,
    ILogger<CheckoutEvictionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var eviction = options.Value.Checkout;
        if (!eviction.Enabled || eviction.SweepInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(eviction.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var report = pool.Evict(eviction, clock);
                    logger.LogInformation(
                        "checkout eviction scanned {Scanned}, deleted {Deleted}, skipped {SkippedInUse}, failed {Failed}, freed {BytesFreed} bytes, {BytesRemaining} bytes remain",
                        report.Scanned, report.Deleted, report.SkippedInUse, report.Failed, report.BytesFreed, report.BytesRemaining);
                    if (report.BytesRemaining > eviction.MaxTotalBytes && eviction.MaxTotalBytes > 0)
                    {
                        logger.LogWarning(
                            "checkout disk budget exceeded: {BytesRemaining} bytes remain over budget {MaxTotalBytes} (excess checkouts in use)",
                            report.BytesRemaining, eviction.MaxTotalBytes);
                    }

                    if (report.Failed > 0)
                    {
                        logger.LogWarning(
                            "checkout eviction failed to delete {Failed} checkouts: {Details}",
                            report.Failed, string.Join("; ", report.FailureDetails));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "checkout eviction sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
    }
}
