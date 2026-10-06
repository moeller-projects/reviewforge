using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class ReviewTelemetry
{
    public static readonly Counter<long> ReviewsStarted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.reviews.started_total");
    public static readonly Counter<long> ReviewsCompleted =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.reviews.completed_total"); // tags: result
    public static readonly Histogram<double> ReviewDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.review.duration_ms", "ms"); // tags: result
    public static readonly Counter<long> TrivialReviews =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.reviews.trivial_total"); // LLM skipped: zero added reviewable lines
    public static readonly Histogram<double> ShardDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.shard.duration_ms", "ms"); // per-shard agent duration
    public static readonly Counter<long> ShardFallback =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.shard.fallback_total"); // shard-cap overflow → legacy single-agent path
    public static readonly Histogram<double> StageDurationMilliseconds =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.stage.duration_ms", "ms"); // tags: stage, result
    public static readonly Counter<long> EnrichmentFailures =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.enrichment.failures_total"); // tags: reason = exception type
    /// <summary>Bot-authored heads suppressed — tags: source = gate | discovery.</summary>
    public static readonly Counter<long> LoopGuardSkips =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.loopguard.skips_total");
    public static readonly Counter<long> ThreadsResolved =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.threads.resolved_total");
    public static readonly Counter<long> ThreadsReplied =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.threads.replied_total");
}
