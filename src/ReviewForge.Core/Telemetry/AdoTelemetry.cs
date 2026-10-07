using System.Diagnostics.Metrics;

namespace ReviewForge.Core.Pipeline;

public static class AdoTelemetry
{
    public static readonly Histogram<double> AdoCallDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.ado.call_duration_ms", "ms"); // tags: pr.operation

    public static readonly Counter<long> AdoCallFailed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.ado.call_failed_total"); // tags: pr.operation
}