using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class QueueTelemetry
{
    public static readonly Counter<long> QueueRejected =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.queue.rejected_total");

    public static readonly Counter<long> QueueReclaimed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.queue.reclaimed_total"); // expired claim re-claimed by a worker

    public static void RegisterGauges(Func<int> queueDepth, Func<int> queueCapacity)
    {
        ReviewForgeTelemetry.Meter.CreateObservableGauge("reviewforge.queue.depth", queueDepth);
        ReviewForgeTelemetry.Meter.CreateObservableGauge("reviewforge.queue.capacity", queueCapacity);
    }
}
