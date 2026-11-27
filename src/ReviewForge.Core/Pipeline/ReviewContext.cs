using System.Diagnostics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Resolve;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline;

/// <summary>
/// Mutable bag carried through the stages. Stages read what earlier stages produced and
/// attach their own output. <see cref="Terminate"/> ends the run gracefully (success,
/// no further stages) — used by the gate when no review is required.
/// </summary>
public sealed class ReviewContext(PrKey pr, DateTimeOffset startedAt, Guid? runId = null, int modelCallLimit = 10_000, TimeProvider? modelCallClock = null) : IDisposable
{
    public PrKey Pr { get; } = pr;
    public Guid RunId { get; } = runId ?? Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = startedAt;
    private Profiling.ModelProfileCollector? _ModelProfile;
    public Profiling.ModelProfileCollector ModelProfile => LazyInitializer.EnsureInitialized(ref _ModelProfile,
        () => new Profiling.ModelProfileCollector(RunId, modelCallLimit, modelCallClock));

    /// <summary>How the run entered the queue (from the dequeued <see cref="ReviewRequest"/>).
    /// The loop guard suppresses discovery-triggered runs on bot-authored heads only.</summary>
    public EnqueueTrigger Trigger { get; set; } = EnqueueTrigger.Manual;

    /// <summary>Explicit manual ordinary-review opt-in to bypass same-head suppression only.</summary>
    public bool ForceReview { get; set; }


    public IReadOnlyList<string> PlannedStages { get; set; } = [];
    public List<StageExecutionDiagnostic> StageExecutions { get; } = [];
    public string? DiagnosticTraceId { get; set; }
    public string? DiagnosticRuleBookVersion { get; set; }
    /// <summary>Host journal may flush only after an existing persistence stage succeeds.</summary>
    public bool DiagnosticsShellExists { get; set; }

    public RunDiagnostics SnapshotDiagnostics()
    {
        var result = Reasoning.Result;
        var narrative = result?.Narrative ?? Reasoning.Collector.Narrative;
        return new(RunId, DiagnosticTraceId, PlannedStages.ToArray(), StageExecutions.ToArray(), new(
            SafeDiagnosticText(result?.RuleBookVersion ?? DiagnosticRuleBookVersion), SafeDiagnosticText(result?.ReviewDepth), "unavailable",
            SafeDiagnosticText(Fetch.PullRequest?.SourceCommitSha), SafeDiagnosticText(Fetch.PullRequest?.TargetCommitSha),
            Fetch.PullRequest is null ? null : Fetch.ChangedFiles.Count, SafeDiagnosticText(narrative?.ReviewSummary),
            SafeDiagnosticText(narrative?.VerificationSummary), SafeDiagnosticText(narrative?.PrSummary),
            (result?.Uncertainties ?? Reasoning.Collector.Uncertainties).Take(20).Select(u => new ReviewUncertainty(
                SafeDiagnosticText(u.Topic)!, SafeDiagnosticText(u.Question)!, SafeDiagnosticText(u.Context))).ToArray(),
            (narrative?.AcceptanceCriteria ?? []).Take(20).Select(a => a with {
                Criterion = SafeDiagnosticText(a.Criterion)!, Evidence = SafeDiagnosticText(a.Evidence) }).ToArray(),
            Repository.ReviewableFiles?.Count,
            (result?.Uncertainties ?? Reasoning.Collector.Uncertainties).Count > 20 ||
            (narrative?.AcceptanceCriteria?.Length ?? 0) > 20));
    }

    private static string? SafeDiagnosticText(string? value)
    {
        if (value is null) return null;
        // Bound work before redaction. Narrative fields are untrusted prose, not a channel for credentials.
        var truncated = value.Length > 2000;
        if (truncated) value = value[..2000];
        value = System.Text.RegularExpressions.Regex.Replace(value,
            "(?i)(?:authorization[\\\"']?\\s*[:=]\\s*[^\\r\\n]+|(?:bearer|basic)\\s+\\S+|(?:api[_-]?key|token|password|secret|pat)[\\\"']?\\s*[:=]\\s*[^\\s,;]+|https?://[^\\s]+|sk-[a-zA-Z0-9_-]+|-----BEGIN[\\s\\S]*)",
            "[redacted]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        return truncated ? value + " [truncated]" : value;
    }

    public FetchOutcome Fetch { get; set; } = new();

    // Stage 2 — gate
    // Gate state shared by review and resolve runs.
    public RunKind RunKind { get; set; } = RunKind.Review;
    public ResolveState? Resolve { get; set; }
    public GateDecision? Gate { get; set; }

    public RepoPreparation Repository { get; set; } = new();
    public IDisposable? RepoLease { get; set; }

    public Classification Classification { get; set; } = new();
    public ReasoningOutcome Reasoning { get; set; } = new();
    public ValidationOutcome Validation { get; set; } = new();
    public AutoFixOutcome AutoFix { get; set; } = new();
    public TriageOutcome Triage { get; set; } = new();
    public PublishOutcome Published { get; set; } = new();

    private readonly List<FindingObservation> _FindingObservations = [];

    /// <summary>Snapshots include collector candidates even if reasoning threw before returning a result.</summary>
    public IReadOnlyList<FindingObservation> SnapshotFindingObservations()
    {
        if (Reasoning.Collector is { } collector)
        {
            foreach (var observation in collector.FindingObservations)
                if (!_FindingObservations.Any(o => o.Id == observation.Id)) _FindingObservations.Add(observation);
        }
        // Also supports callers supplying a result directly (without collector entries).
        foreach (var finding in (Reasoning.Result?.Findings ?? []).Concat(Validation.AcceptedFindings))
            if (!_FindingObservations.Any(o => o.DedupeKey == finding.DedupeKey))
                _FindingObservations.Add(FindingObservation.Create(finding));
        return _FindingObservations.Select(o => Published.NewlyPostedThreadIds.ContainsKey(o.DedupeKey ?? string.Empty)
            ? o with { NewlyPosted = true } : o).ToArray();
    }

    public void ObserveValidation(RichFinding finding, FindingValidationStatus status, string reason)
        => UpdateObservation(finding, o => o with
        {
            Validation = status, ValidationReason = reason, ValidationProvenance = "anchor-diff-validation",
            VerificationReason = status == FindingValidationStatus.Rejected && o.Verification == FindingVerificationStatus.Unverified
                ? "validation-rejected" : o.VerificationReason,
        });

    public void ObserveVerification(RichFinding finding, FindingVerificationStatus status, string reason, string provenance)
        => UpdateObservation(finding, o => o with
        {
            Verification = status, VerificationReason = reason.Length > 1000 ? reason[..1000] : reason,
            VerificationProvenance = provenance,
        });

    private void UpdateObservation(RichFinding finding, Func<FindingObservation, FindingObservation> update)
    {
        SnapshotFindingObservations();
        for (var i = 0; i < _FindingObservations.Count; i++)
            if (!_FindingObservations[i].PreviouslyKnown && _FindingObservations[i].DedupeKey == finding.DedupeKey)
                _FindingObservations[i] = update(_FindingObservations[i]);
    }

    /// <summary>True once a stage ended the run early (always a successful outcome).</summary>
    public bool Terminated { get; private set; }

    public string? TerminationReason { get; private set; }

    /// <summary>Optional host-owned guard checked immediately before external publication.</summary>
    public Func<bool>? PublishGuard { get; set; }

    /// <summary>Trace context of the discovery enqueue that created this run (span link source).</summary>
    public ActivityContext? EnqueueContext { get; set; }

    public void Dispose()
    {
        Repository.Dispose();
        RepoLease?.Dispose();
        RepoLease = null;
    }

    public string RequireRepoDir()
    {
        if (Repository.RepoDir is not { } dir)
        {
            throw new InvalidOperationException(
                $"stage ordering violation: {nameof(Repository.RepoDir)} is null but required");
        }

        return dir;
    }

    public PullRequest RequirePullRequest()
        => Fetch.PullRequest ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(PullRequest)} is null but required (fetch stage must run first)");

    public ReviewResult RequireResult()
        => Reasoning.Result ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(Reasoning.Result)} is null but required (reasoning stage must run first)");

    public ResolveState RequireResolveState()
        => Resolve ?? throw new InvalidOperationException("resolve state is not initialized for this run");

    public void Terminate(string reason)
    {
        Terminated = true;
        TerminationReason = reason;
    }
}
