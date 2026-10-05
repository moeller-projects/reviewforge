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
    public const string TagResult = "result"; // completed | skipped | failed
    public const string TagReason = "reason";
    public const string TagWarmed = "warmed"; // true when a discovery mirror warmup prefetched this head
    public const string TagKind = "kind"; // pooled | private (checkout metrics)
    public const string TagShards = "shards"; // shard count on sharded review run metrics

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

    public static readonly Counter<long> QueueReclaimed =
        Meter.CreateCounter<long>("reviewforge.queue.reclaimed_total"); // expired claim re-claimed by a worker

    // ---- Review run lifecycle ----
    public static readonly Counter<long> ReviewsStarted =
        Meter.CreateCounter<long>("reviewforge.reviews.started_total");

    public static readonly Counter<long> ReviewsCompleted =
        Meter.CreateCounter<long>("reviewforge.reviews.completed_total"); // tags: result

    public static readonly Histogram<double> ReviewDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.review.duration_ms", "ms"); // tags: result

    public static readonly Counter<long> TrivialReviews =
        Meter.CreateCounter<long>("reviewforge.reviews.trivial_total"); // LLM skipped: zero added reviewable lines

    public static readonly Histogram<double> ShardDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.shard.duration_ms", "ms"); // per-shard agent duration

    public static readonly Counter<long> ShardFallback =
        Meter.CreateCounter<long>("reviewforge.shard.fallback_total"); // shard-cap overflow → legacy single-agent path

    public static readonly Histogram<double> StageDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.stage.duration_ms", "ms"); // tags: stage, result

    // ---- LLM usage ----
    public static readonly Counter<long> LlmTokens =
        Meter.CreateCounter<long>("reviewforge.llm.tokens_total", "{token}"); // tags: token_type, model

    public static readonly Counter<long> LlmCachedTokens =
        Meter.CreateCounter<long>("reviewforge.llm.tokens.cached_total", "{token}"); // tags: model

    public static readonly Counter<long> LlmRequests =
        Meter.CreateCounter<long>("reviewforge.llm.requests_total"); // tags: model

    public static readonly Histogram<double> LlmGovernorWait =
        Meter.CreateHistogram<double>("reviewforge.llm.governor.wait_ms", "ms"); // slot acquisition wait

    public static readonly Counter<long> LlmGovernorTimeouts =
        Meter.CreateCounter<long>("reviewforge.llm.governor.timeout_total"); // slot acquisition timed out

    // ---- Agent loop quality ----
    public static readonly Histogram<int> AgentIterations =
        Meter.CreateHistogram<int>("reviewforge.agent.iterations", "{iteration}"); // model turns per review

    public static readonly Counter<long> AgentTaskDoneMissing =
        Meter.CreateCounter<long>("reviewforge.agent.task_done_missing_total"); // hit iteration cap w/o task_done — tags: model

    // ---- Enrichment (fail-safe, non-fatal) ----
    public static readonly Counter<long> EnrichmentFailures =
        Meter.CreateCounter<long>("reviewforge.enrichment.failures_total"); // tags: reason = exception type

    // ---- Claims ----
    public static readonly Counter<long> ClaimAcquired =
        Meter.CreateCounter<long>("reviewforge.claims.acquired_total");

    public static readonly Counter<long> ClaimRejected =
        Meter.CreateCounter<long>("reviewforge.claims.rejected_total"); // TryClaim returned false

    public static readonly Counter<long> ClaimRenewed =
        Meter.CreateCounter<long>("reviewforge.claims.renewed_total");

    public static readonly Counter<long> ClaimRenewalFailed =
        Meter.CreateCounter<long>("reviewforge.claims.renewal_failed_total"); // Renew returned false

    public static readonly Counter<long> ClaimExpired =
        Meter.CreateCounter<long>("reviewforge.claims.expired_total"); // expired entry observed

    // ---- Discovery ----
    public static readonly Histogram<double> DiscoverySweepDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.discovery.sweep.duration_ms", "ms");

    public static readonly Counter<long> DiscoveryCandidateErrors =
        Meter.CreateCounter<long>("reviewforge.discovery.candidate_errors_total"); // faulting candidate isolated as a skip

    public static readonly Counter<long> DiscoveryWarmup =
        Meter.CreateCounter<long>("reviewforge.discovery.warmup.total"); // result: completed | failed

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
        Meter.CreateCounter<long>("reviewforge.ado.call_failed_total"); // tags: ado.operation

    // ---- Findings ----
    public static readonly Counter<long> FindingsAccepted =
        Meter.CreateCounter<long>("reviewforge.findings.accepted_total");

    public static readonly Counter<long> FindingsRejected =
        Meter.CreateCounter<long>("reviewforge.findings.rejected_total"); // tags: reason

    public static readonly Counter<long> FindingsPosted =
        Meter.CreateCounter<long>("reviewforge.findings.posted_total"); // tags: kind = inline|general

    // ---- Findings verifier (fail-open challenge stage) ----
    /// <summary>Findings removed by the verifier; per-rule rate is the FP metric — tags: rule.</summary>
    public static readonly Counter<long> FindingsVerifierRejected =
        Meter.CreateCounter<long>("reviewforge.findings.verifier_rejected_total"); // tags: rule

    public static readonly Counter<long> FindingsVerifierFailures =
        Meter.CreateCounter<long>("reviewforge.findings.verifier_failures_total"); // tags: reason

    public static readonly Histogram<double> FindingsVerifierDurationMilliseconds =
        Meter.CreateHistogram<double>("reviewforge.findings.verifier_duration_ms", "ms");

    // ---- Auto-fix ----
    public static readonly Counter<long> FixesApplied =
        Meter.CreateCounter<long>("reviewforge.fixes.applied_total"); // tags: origin, rule (thread-command for /fixit)

    public static readonly Counter<long> FixesDeclined =
        Meter.CreateCounter<long>("reviewforge.fixes.declined_total"); // tags: origin, rule

    public static readonly Counter<long> DeterministicGuardSkipped =
        Meter.CreateCounter<long>("reviewforge.autofix.deterministic.guard_skipped");

    public static readonly Counter<long> CommandedWatermarkSkipped =
        Meter.CreateCounter<long>("reviewforge.autofix.commanded.skipped_watermark");

    // ---- Auto-fix CommitOnHead write path ----
    /// <summary>Commits created by stage 7.7 — tags: granularity.</summary>
    public static readonly Counter<long> AutoFixCommits =
        Meter.CreateCounter<long>("reviewforge.autofix.commits_total");

    public static readonly Counter<long> AutoFixPushes =
        Meter.CreateCounter<long>("reviewforge.autofix.pushes_total");

    /// <summary>Push failures — tags: reason = pin | rejected.</summary>
    public static readonly Counter<long> AutoFixPushFailures =
        Meter.CreateCounter<long>("reviewforge.autofix.push_failures_total");

    /// <summary>Fixes whose tree application failed (drift/unreadable) — tags: rule.</summary>
    public static readonly Counter<long> AutoFixApplyFailed =
        Meter.CreateCounter<long>("reviewforge.autofix.apply_failed_total");

    /// <summary>Fixes published as suggestions instead of commits — tags:
    /// reason = no_source_ref | commit_failed | all_degraded.</summary>
    public static readonly Counter<long> AutoFixDegradedToSuggestion =
        Meter.CreateCounter<long>("reviewforge.autofix.degraded_to_suggestion_total");

    /// <summary>Crash-recovery replies posted by a later run's publish reconciliation.</summary>
    public static readonly Counter<long> AutoFixReconciledReplies =
        Meter.CreateCounter<long>("reviewforge.autofix.reconciled_replies_total");

    // ---- Resolve pipeline ----
    public static readonly Counter<long> ResolveThreadsTriaged =
        Meter.CreateCounter<long>("reviewforge.resolve.threads_triaged_total");

    public static readonly Counter<long> ResolveFixesApplied =
        Meter.CreateCounter<long>("reviewforge.resolve.fixes_applied_total");

    public static readonly Counter<long> ResolveFixesDeclined =
        Meter.CreateCounter<long>("reviewforge.resolve.fixes_declined_total");

    public static readonly Counter<long> ResolveVerifyFailed =
        Meter.CreateCounter<long>("reviewforge.resolve.verify_failed_total");

    public static readonly Counter<long> ResolveCommitsPushed =
        Meter.CreateCounter<long>("reviewforge.resolve.commits_pushed_total");

    public static readonly Counter<long> ResolvePushFailures =
        Meter.CreateCounter<long>("reviewforge.resolve.push_failures_total");

    public static readonly Counter<long> ResolveRepliesPosted =
        Meter.CreateCounter<long>("reviewforge.resolve.replies_posted_total");

    public static readonly Counter<long> ResolveRepliesDeduped =
        Meter.CreateCounter<long>("reviewforge.resolve.replies_deduped_total");

    public static readonly Counter<long> ResolveDeferred =
        Meter.CreateCounter<long>("reviewforge.resolve.deferred_total");

    public static readonly Counter<long> ResolveEvidenceDowngrades =
        Meter.CreateCounter<long>("reviewforge.resolve.evidence_downgrades_total");

    public static readonly Counter<long> ResolveUnknownVerdicts =
        Meter.CreateCounter<long>("reviewforge.resolve.unknown_verdicts_total");

    // ---- Loop guard ----
    /// <summary>Bot-authored heads suppressed — tags: source = gate | discovery.</summary>
    public static readonly Counter<long> LoopGuardSkips =
        Meter.CreateCounter<long>("reviewforge.loopguard.skips_total");

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
        Func<int> activeClaims, Func<int> checkoutDirs,
        Func<int>? llmInflight = null)
    {
        Meter.CreateObservableGauge("reviewforge.queue.depth", queueDepth);
        Meter.CreateObservableGauge("reviewforge.queue.capacity", queueCapacity);
        Meter.CreateObservableGauge("reviewforge.claims.active", activeClaims);
        Meter.CreateObservableGauge("reviewforge.checkout.pool_size", checkoutDirs);
        if (llmInflight is not null)
        {
            Meter.CreateObservableGauge("reviewforge.llm.governor.inflight", llmInflight);
        }
    }
}