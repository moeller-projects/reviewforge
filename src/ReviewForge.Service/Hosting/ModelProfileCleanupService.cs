using Microsoft.Extensions.Options;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>Awaited bounded cleanup. Failures propagate to the host, never a detached background write.</summary>
public sealed class ModelProfileCleanupService(IModelProfileStore store, IOptions<ModelProfilingOptions> options,
    TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes), clock);
        do
        {
            await store.ExpireModelProfilesAsync(clock.GetUtcNow(), options.Value.CleanupBatchSize, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
