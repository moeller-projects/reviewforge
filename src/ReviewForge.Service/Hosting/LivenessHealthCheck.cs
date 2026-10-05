using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ReviewForge.Service;

/// <summary>Process liveness check with no dependency probes.</summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    public const string Name = "process-liveness";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
        => Task.FromResult(HealthCheckResult.Healthy("process is running"));
}