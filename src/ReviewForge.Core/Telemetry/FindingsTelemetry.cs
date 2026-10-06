using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class FindingsTelemetry
{
    public static readonly Counter<long> FindingsAccepted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.findings.accepted_total");

    public static readonly Counter<long> FindingsRejected =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.findings.rejected_total"); // tags: reason

    public static readonly Counter<long> FindingsPosted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.findings.posted_total"); // tags: kind = inline|general

    /// <summary>Findings removed by the verifier; per-rule rate is the FP metric — tags: rule.</summary>
    public static readonly Counter<long> FindingsVerifierRejected =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.findings.verifier_rejected_total"); // tags: rule

    public static readonly Counter<long> FindingsVerifierFailures =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.findings.verifier_failures_total"); // tags: reason

    public static readonly Histogram<double> FindingsVerifierDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.findings.verifier_duration_ms", "ms");
}
