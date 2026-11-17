using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Core.Telemetry;

namespace ReviewForge.Service;

/// <summary>
/// Registers the observable gauges exactly once at startup with live delegates, decoupling
/// gauge publication from singleton construction order.
/// </summary>
public sealed class TelemetryGaugeRegistration(
    IReviewQueue queue,
    InFlightClaims claims,
    RepoCheckoutPool pool) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        QueueTelemetry.RegisterGauges(
            () => queue.ApproximateDepth,
            () => queue.Capacity);
        ClaimsTelemetry.RegisterGauges(() => claims.ActiveCount);
        CheckoutTelemetry.RegisterGauges(() => pool.CheckoutDirectoryCount());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}