using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReviewForge.Core.Pipeline;

/// <summary>Single ActivitySource/Meter for the whole pipeline; the host wires them into OTel.</summary>
public static class ReviewForgeTelemetry
{
    public const string SourceName = "ReviewForge";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    public static readonly Meter Meter = new(SourceName, "1.0.0");

    // ---- Tag name conventions (trace spans; only low-cardinality subsets on metrics) ----
    public const string TagRunId = "reviewforge.run.id";
    public const string TagPrId = "reviewforge.pr.id";
    public const string TagRepoId = "reviewforge.repo.id";
    public const string TagOrg = "reviewforge.pr.org";
    public const string TagProject = "reviewforge.pr.project";
    public const string TagHeadSha = "reviewforge.pr.head_sha";
    public const string TagStage = "stage";
    public const string TagResult = "result";      // completed | skipped | failed
    public const string TagReason = "reason";

    // ---- Checkouts (existing, unchanged) ----
    public static readonly Histogram<double> CheckoutAcquireMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.checkout.acquire_ms", "ms");
    public static readonly UpDownCounter<int> CheckoutActive =
        Meter.CreateUpDownCounter<int>("reviewforge.checkout.active");
    public static readonly Counter<long> CheckoutEvicted =
        Meter.CreateCounter<long>("reviewforge.checkout.evicted_total");

    /// <summary>Bytes freed by checkout eviction (CheckoutEvictionReport.BytesFreed).</summary>
    public static readonly Counter<long> CheckoutEvictedBytes =
        Meter.CreateCounter<long>("reviewforge.checkout.evicted_bytes_total", "By");

    // ---- Queue ----
    public static readonly Counter<long> QueueRejected =
        Meter.CreateCounter<long>("reviewforge.queue.rejected_total");

    // ---- Review run lifecycle ----
    public static readonly Counter<long> ReviewsStarted =
        Meter.CreateCounter<long>("reviewforge.reviews.started_total");
    public static readonly Counter<long> ReviewsCompleted =
        Meter.CreateCounter<long>("reviewforge.reviews.completed_total");   // tags: result
    public static readonly Histogram<double> ReviewDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.review.duration_ms", "ms"); // tags: result
    public static readonly Histogram<double> StageDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.stage.duration_ms", "ms");  // tags: stage, result

    // ---- LLM usage ----
    public static readonly Counter<long> LlmTokens =
        Meter.CreateCounter<long>("reviewforge.llm.tokens_total", "{token}"); // tags: token_type, model
    public static readonly Counter<long> LlmRequests =
        Meter.CreateCounter<long>("reviewforge.llm.requests_total");           // tags: model

    // ---- Agent loop quality ----
    public static readonly Histogram<int> AgentIterations =
        Meter.CreateHistogram<int>("reviewforge.agent.iterations", "{iteration}"); // model turns per review
    public static readonly Counter<long> AgentTaskDoneMissing =
        Meter.CreateCounter<long>("reviewforge.agent.task_done_missing_total");    // hit iteration cap w/o task_done — tags: model

    // ---- Enrichment (fail-safe, non-fatal) ----
    public static readonly Counter<long> EnrichmentFailures =
        Meter.CreateCounter<long>("reviewforge.enrichment.failures_total");        // tags: reason = exception type

    // ---- Claims ----
    public static readonly Counter<long> ClaimAcquired =
        Meter.CreateCounter<long>("reviewforge.claims.acquired_total");
    public static readonly Counter<long> ClaimRejected =
        Meter.CreateCounter<long>("reviewforge.claims.rejected_total");   // TryClaim returned false
    public static readonly Counter<long> ClaimRenewed =
        Meter.CreateCounter<long>("reviewforge.claims.renewed_total");
    public static readonly Counter<long> ClaimRenewalFailed =
        Meter.CreateCounter<long>("reviewforge.claims.renewal_failed_total"); // Renew returned false
    public static readonly Counter<long> ClaimExpired =
        Meter.CreateCounter<long>("reviewforge.claims.expired_total");    // expired entry observed

    // ---- Discovery ----
    public static readonly Histogram<double> DiscoverySweepDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.discovery.sweep_duration_ms", "ms");
    public static readonly Counter<long> DiscoveryCandidates =
        Meter.CreateCounter<long>("reviewforge.discovery.candidates_total");
    public static readonly Counter<long> DiscoveryEnqueued =
        Meter.CreateCounter<long>("reviewforge.discovery.enqueued_total");
    public static readonly Counter<long> DiscoverySkipped =
        Meter.CreateCounter<long>("reviewforge.discovery.skipped_total"); // tags: reason (normalized)

    // ---- ADO ----
    public static readonly Histogram<double> AdoCallDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.ado.call_duration_ms", "ms"); // tags: ado.operation
    public static readonly Counter<long> AdoCallFailed =
        Meter.CreateCounter<long>("reviewforge.ado.call_failed_total");          // tags: ado.operation

    // ---- Findings ----
    public static readonly Counter<long> FindingsAccepted =
        Meter.CreateCounter<long>("reviewforge.findings.accepted_total");
    public static readonly Counter<long> FindingsRejected =
        Meter.CreateCounter<long>("reviewforge.findings.rejected_total");  // tags: reason
    public static readonly Counter<long> FindingsPosted =
        Meter.CreateCounter<long>("reviewforge.findings.posted_total");    // tags: kind = inline|general

    // ---- Auto-fix ----
    public static readonly Counter<long> FixesApplied =
        Meter.CreateCounter<long>("reviewforge.fixes.applied_total");    // tags: origin, rule (thread-command for /rf fix)
    public static readonly Counter<long> FixesDeclined =
        Meter.CreateCounter<long>("reviewforge.fixes.declined_total");   // tags: origin, rule

    public static readonly Counter<long> ThreadsResolved =
        Meter.CreateCounter<long>("reviewforge.threads.resolved_total");
    public static readonly Counter<long> ThreadsReplied =
        Meter.CreateCounter<long>("reviewforge.threads.replied_total");

    /// <summary>
    /// Registers observable gauges exactly once at host startup. Callers pass live
    /// delegates so the gauge never depends on instance construction order.
    /// </summary>
    public static void RegisterGauges(
        Func<int> queueDepth, Func<int> queueCapacity,
        Func<int> activeClaims, Func<int> checkoutDirs)
    {
        Meter.CreateObservableGauge("reviewforge.queue.depth", queueDepth);
        Meter.CreateObservableGauge("reviewforge.queue.capacity", queueCapacity);
        Meter.CreateObservableGauge("reviewforge.claims.active", activeClaims);
        Meter.CreateObservableGauge("reviewforge.checkout.pool_size", checkoutDirs);
    }
}
