using System.Diagnostics.Metrics;

namespace ReviewForge.Core.Pipeline;

public static class DiscoveryTelemetry
{
    public static readonly Histogram<double> DiscoverySweepDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.discovery.sweep.duration_ms", "ms");

    public static readonly Counter<long> DiscoveryCandidateErrors =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.discovery.candidate_errors_total"); // faulting candidate isolated as a skip

    public static readonly Counter<long> DiscoveryWarmup =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.discovery.warmup.total"); // result: completed | failed

    public static readonly Counter<long> DiscoveryCandidates =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.discovery.candidates_total");

    public static readonly Counter<long> DiscoveryEnqueued =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.discovery.enqueued_total");

    public static readonly Counter<long> DiscoverySkipped =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.discovery.skipped_total"); // tags: reason (normalized)
}