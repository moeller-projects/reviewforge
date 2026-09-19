using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace ReviewForge.Core.Pipeline;

/// <summary>Single ActivitySource for the whole pipeline; the service host wires it into OTel.</summary>
public static class ReviewForgeTelemetry
{
    public const string SourceName = "ReviewForge";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Histogram<double> CheckoutAcquireMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.checkout.acquire_ms", "ms");
    public static readonly UpDownCounter<int> CheckoutActive =
        Meter.CreateUpDownCounter<int>("reviewforge.checkout.active");
    public static readonly Counter<long> CheckoutEvicted =
        Meter.CreateCounter<long>("reviewforge.checkout.evicted_total");
}