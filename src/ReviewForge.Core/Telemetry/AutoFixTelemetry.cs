using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class AutoFixTelemetry
{
    public static readonly Counter<long> FixesApplied =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.fixes.applied_total"); // tags: origin, rule (thread-command for /fixit)

    public static readonly Counter<long> FixesDeclined =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.fixes.declined_total"); // tags: origin, rule

    public static readonly Counter<long> DeterministicGuardSkipped =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.deterministic.guard_skipped");

    public static readonly Counter<long> CommandedWatermarkSkipped =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.commanded.skipped_watermark");

    /// <summary>Commits created by stage 7.7 — tags: granularity.</summary>
    public static readonly Counter<long> AutoFixCommits =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.commits_total");

    public static readonly Counter<long> AutoFixPushes =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.pushes_total");

    /// <summary>Push failures — tags: reason = pin | rejected.</summary>
    public static readonly Counter<long> AutoFixPushFailures =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.push_failures_total");

    /// <summary>Fixes whose tree application failed (drift/unreadable) — tags: rule.</summary>
    public static readonly Counter<long> AutoFixApplyFailed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.apply_failed_total");

    /// <summary>Fixes published as suggestions instead of commits — tags:
    /// reason = no_source_ref | commit_failed | all_degraded.</summary>
    public static readonly Counter<long> AutoFixDegradedToSuggestion =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.degraded_to_suggestion_total");

    /// <summary>Crash-recovery replies posted by a later run's publish reconciliation.</summary>
    public static readonly Counter<long> AutoFixReconciledReplies =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.autofix.reconciled_replies_total");
}
