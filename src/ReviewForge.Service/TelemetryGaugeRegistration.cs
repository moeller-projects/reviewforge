using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Workspaces;
using ReviewForge.Infrastructure.Chat;
using ReviewForge.Service.Queue;

namespace ReviewForge.Service;

/// <summary>
/// Registers the observable gauges exactly once at startup with live delegates, decoupling
/// gauge publication from singleton construction order.
/// </summary>
public sealed class TelemetryGaugeRegistration(
    IReviewQueue queue,
    InFlightClaims claims,
    RepoCheckoutPool pool,
    LlmGovernor governor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ReviewForgeTelemetry.RegisterGauges(
            () => queue.ApproximateDepth,
            () => queue.Capacity,
            () => claims.ActiveCount,
            () => pool.CheckoutDirectoryCount(),
            () => governor.Inflight);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
