using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>
/// Registers the observable gauges exactly once at startup with live delegates, decoupling
/// gauge publication from singleton construction order.
/// </summary>
public sealed class TelemetryGaugeRegistration(
    ReviewQueue queue,
    InFlightClaims claims,
    RepoCheckoutPool pool) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ReviewForgeTelemetry.RegisterGauges(
            () => queue.ApproximateDepth,
            () => queue.Capacity,
            () => claims.ActiveCount,
            () => pool.CheckoutDirectoryCount());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
