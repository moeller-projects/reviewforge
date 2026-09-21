using System.Diagnostics;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;

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

    // Stage 1 — fetch
    public PullRequest? PullRequest { get; set; }
    public IReadOnlyList<WorkItem> WorkItems { get; set; } = [];
    public IReadOnlyList<ReviewThread> Threads { get; set; } = [];
    private IReadOnlyList<ChangedFile> _changedFileManifest = [];
    private IReadOnlyList<string>? _changedFiles;

    public IReadOnlyList<ChangedFile> ChangedFileManifest
    {
        get => _changedFileManifest;
        set
        {
            _changedFileManifest = value;
            _changedFiles = null; // invalidate the projection cache
        }
    }

    /// <summary>Lazy projection of <see cref="ChangedFileManifest"/>; computed once per assignment.</summary>
    public IReadOnlyList<string> ChangedFiles
        => _changedFiles ??= ChangedFileManifest.Select(file => file.Path).ToArray();
    public CurrentUser? CurrentUser { get; set; }
    public PriorRun? PriorRun { get; set; }

    // Stage 2 — gate
    public GateDecision? Gate { get; set; }

    // Stage 3 — repository

    // Stage 3 — repository lease held until the worker disposes this context.
    public IDisposable? RepoLease { get; set; }
    public string? RepoDir { get; set; }
    public string DiffText { get; set; } = string.Empty;
    public DiffIndex? Diff { get; set; }

    /// <summary>Manifest ∩ diff files that carry reviewable text (set by stage 3).</summary>
    public IReadOnlyCollection<string>? ReviewableFiles { get; set; }

    // Stage 4 — classify
    public ReviewKind Kind { get; set; } = ReviewKind.Full;
    public IReadOnlyList<PendingReply> PendingReplies { get; set; } = [];

    // Stage 5/6 — reasoning
    public ContextStore ContextStore { get; } = new();
    public ReviewCollector Collector { get; set; } = new();
    public ReviewResult? Result { get; set; }

    // Stage 7 — validation output: findings accepted for posting
    public IReadOnlyList<RichFinding> AcceptedFindings { get; set; } = [];

    // Stage 8 — triage
    public IReadOnlyList<TriageOperation> TriagePlan { get; set; } = [];
    public IReadOnlyList<int> UnansweredThreads { get; set; } = [];

    // Stage 9 — publish
    public IReadOnlyDictionary<string, int> PostedThreadIds { get; set; } = new Dictionary<string, int>();

    /// <summary>True once a stage ended the run early (always a successful outcome).</summary>
    public bool Terminated { get; private set; }

    public string? TerminationReason { get; private set; }

    /// <summary>Optional host-owned guard checked immediately before external publication.</summary>
    public Func<bool>? PublishGuard { get; set; }

    /// <summary>Trace context of the discovery enqueue that created this run (span link source).</summary>
    public ActivityContext? EnqueueContext { get; set; }

    public void Dispose()
    {
        RepoLease?.Dispose();
        RepoLease = null;
    }

    public string RequireRepoDir()
    {
        if (RepoDir is not { } dir)
        {
            throw new InvalidOperationException(
                $"stage ordering violation: {nameof(RepoDir)} is null but required");
        }

        return dir;
    }

    public void Terminate(string reason)
    {
        Terminated = true;
        TerminationReason = reason;
    }
}