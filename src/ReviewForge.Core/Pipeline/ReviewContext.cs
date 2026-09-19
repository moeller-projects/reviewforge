using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;
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
    public IReadOnlyList<ChangedFile> ChangedFileManifest { get; set; } = [];
    public IReadOnlyList<string> ChangedFiles => ChangedFileManifest.Select(file => file.Path).ToArray();
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

    public void Terminate(string reason)
    {
        Terminated = true;
        TerminationReason = reason;
    }

    public void Dispose()
    {
        RepoLease?.Dispose();
        RepoLease = null;
    }
}