using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class ResolveTelemetry
{
    public static readonly Counter<long> ResolveThreadsTriaged =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.threads_triaged_total");
    public static readonly Counter<long> ResolveFixesApplied =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.fixes_applied_total");
    public static readonly Counter<long> ResolveFixesDeclined =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.fixes_declined_total");
    public static readonly Counter<long> ResolveVerifyFailed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.verify_failed_total");
    public static readonly Counter<long> ResolveCommitsPushed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.commits_pushed_total");
    public static readonly Counter<long> ResolvePushFailures =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.push_failures_total");
    public static readonly Counter<long> ResolveRepliesPosted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.replies_posted_total");
    public static readonly Counter<long> ResolveRepliesDeduped =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.replies_deduped_total");
    public static readonly Counter<long> ResolveDeferred =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.deferred_total");
    public static readonly Counter<long> ResolveEvidenceDowngrades =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.evidence_downgrades_total");
    public static readonly Counter<long> ResolveUnknownVerdicts =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.resolve.unknown_verdicts_total");
}
