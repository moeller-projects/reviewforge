using System.Diagnostics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline;

/// <summary>
/// Mutable bag carried through the stages. Stages read what earlier stages produced and
/// attach their own output. <see cref="Terminate"/> ends the run gracefully (success,
/// no further stages) — used by the gate when no review is required.
/// </summary>
public sealed class ReviewContext(PrKey pr, DateTimeOffset startedAt, Guid? runId = null) : IDisposable
{
    public PrKey Pr { get; } = pr;
    public Guid RunId { get; } = runId ?? Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>How the run entered the queue (from the dequeued <see cref="ReviewRequest"/>).
    /// The loop guard suppresses discovery-triggered runs on bot-authored heads only.</summary>
    public EnqueueTrigger Trigger { get; set; } = EnqueueTrigger.Manual;


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
