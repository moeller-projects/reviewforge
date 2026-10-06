using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class CheckoutTelemetry
{
    public static readonly Histogram<double> CheckoutAcquireMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.checkout.acquire_ms", "ms");

    public static readonly UpDownCounter<int> CheckoutActive =
        ReviewForgeTelemetry.Meter.CreateUpDownCounter<int>("reviewforge.checkout.active");

    public static readonly Counter<long> CheckoutEvicted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.checkout.evicted_total");

    /// <summary>Bytes freed by checkout eviction (CheckoutEvictionReport.BytesFreed).</summary>
    public static readonly Counter<long> CheckoutEvictedBytes =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.checkout.evicted_bytes_total", "By");

    public static void RegisterGauges(Func<int> checkoutDirs)
    {
        ReviewForgeTelemetry.Meter.CreateObservableGauge("reviewforge.checkout.pool_size", checkoutDirs);
    }
}
