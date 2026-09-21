using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>
/// Readiness probe: the review workflow is useless when the finding store is unreachable,
/// so <c>/health</c> must reflect store connectivity rather than report healthy unconditionally.
/// </summary>
public sealed class StoreHealthCheck(IFindingStore store) : IHealthCheck
{
    private static readonly PrKey Probe = new("__health__", "__health__", "__health__", int.MinValue);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await store.GetKnownDedupeKeysAsync(Probe, ct).ConfigureAwait(false);
            return HealthCheckResult.Healthy("finding store reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("finding store unreachable", ex);
        }
    }
}