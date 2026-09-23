using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>
/// Readiness probe: the review workflow is useless when the finding store is unreachable,
/// so <c>/health</c> must reflect store connectivity rather than report healthy unconditionally.
/// </summary>
public sealed class StoreHealthCheck(IFindingStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await store.PingAsync(ct).ConfigureAwait(false);
            return HealthCheckResult.Healthy("finding store reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("finding store unreachable", ex);
        }
    }
}