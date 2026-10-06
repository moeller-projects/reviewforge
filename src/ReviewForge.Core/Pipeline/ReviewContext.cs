using System.Diagnostics;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
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

    /// <summary>How the run entered the queue (from the dequeued <see cref="ReviewRequest"/>).
    /// The loop guard suppresses discovery-triggered runs on bot-authored heads only.</summary>
    public EnqueueTrigger Trigger { get; set; } = EnqueueTrigger.Manual;


    public FetchOutcome Fetch { get; set; } = new();

    // Temporary forwarding properties; removed when all stages and fixtures consume Fetch.
    public PullRequest? PullRequest
    {
        get => Fetch.PullRequest;
        set => Fetch = Fetch with {PullRequest = value};
    }
    public IReadOnlyList<WorkItem> WorkItems
    {
        get => Fetch.WorkItems;
        set => Fetch = Fetch with {WorkItems = value};
    }
    public IReadOnlyList<ReviewThread> Threads
    {
        get => Fetch.Threads;
        set => Fetch = Fetch with {Threads = value};
    }
    public IReadOnlyList<ChangedFile> ChangedFileManifest
    {
        get => Fetch.ChangedFileManifest;
        set => Fetch = Fetch with {ChangedFileManifest = value};
    }
    public IReadOnlyList<string> ChangedFiles => Fetch.ChangedFiles;
    public CurrentUser? CurrentUser
    {
        get => Fetch.CurrentUser;
        set => Fetch = Fetch with {CurrentUser = value};
    }
    public PriorRun? PriorRun
    {
        get => Fetch.PriorRun;
        set => Fetch = Fetch with {PriorRun = value};
    }

    /// <summary>DedupeKeys whose live threads are Fixed/Closed in the fetched snapshot.</summary>
    public IReadOnlySet<string> ResolvedKeys
    {
        get => Fetch.ResolvedKeys;
        set => Fetch = Fetch with {ResolvedKeys = value};
    }

    // Stage 2 — gate
    // Gate state shared by review and resolve runs.
    public RunKind RunKind { get; set; } = RunKind.Review;
    public ResolveState? Resolve { get; set; }
    public GateDecision? Gate { get; set; }

    public RepoPreparation Repository { get; set; } = new();
    public IDisposable? RepoLease { get; set; }
    public string? RepoDir
    {
        get => Repository.RepoDir;
        set => Repository = Repository with {RepoDir = value};
    }
    public string DiffText
    {
        get => Repository.DiffText;
        set => Repository = Repository with {DiffText = value};
    }
    public DiffIndex? Diff
    {
        get => Repository.Diff;
        set => Repository = Repository with {Diff = value};
    }

    /// <summary>Manifest ∩ diff files that carry reviewable text (set by stage 3).</summary>
    public IReadOnlyCollection<string>? ReviewableFiles
    {
        get => Repository.ReviewableFiles;
        set => Repository = Repository with {ReviewableFiles = value};
    }

    // Temporary forwarding properties; overlap ownership moves to RepoPreparation in this phase.
    public ThreadsRefreshOverlap? PendingThreadsRefresh
    {
        get => Repository.PendingThreadsRefresh;
        set => Repository = Repository with {PendingThreadsRefresh = value};
    }
    public DateTimeOffset? RepoPreparedAt
    {
        get => Repository.RepoPreparedAt;
        set => Repository = Repository with {RepoPreparedAt = value};
    }
    public Task<string?>? PendingEnrichment
    {
        get => Repository.PendingEnrichment;
        set => Repository = Repository with {PendingEnrichment = value};
    }

    // Stage 4 — classify
    public ReviewKind Kind { get; set; } = ReviewKind.Full;
    public IReadOnlyList<PendingReply> PendingReplies { get; set; } = [];

    public ReasoningOutcome Reasoning { get; set; } = new();
    public ContextStore ContextStore => Reasoning.ContextStore;
    public ReviewCollector Collector
    {
        get => Reasoning.Collector;
        set => Reasoning = Reasoning with {Collector = value};
    }
    public ReviewResult? Result
    {
        get => Reasoning.Result;
        set => Reasoning = Reasoning with {Result = value};
    }

    // Stage 7 — validation output.
    public ValidationOutcome Validation { get; set; } = new();
    public IReadOnlyList<RichFinding> AcceptedFindings
    {
        get => Validation.AcceptedFindings;
        set => Validation = Validation with {AcceptedFindings = value};
    }

    // Stage 8 — auto-fix output.
    public AutoFixOutcome AutoFix { get; set; } = new();
    public IReadOnlyList<AutoFix.AppliedFix> AppliedFixes
    {
        get => AutoFix.AppliedFixes;
        set => AutoFix = AutoFix with {AppliedFixes = value};
    }
    public string? PushedHeadSha
    {
        get => AutoFix.PushedHeadSha;
        set => AutoFix = AutoFix with {PushedHeadSha = value};
    }
    public IReadOnlyList<AutoFix.FixCommand> FixCommands
    {
        get => AutoFix.FixCommands;
        set => AutoFix = AutoFix with {FixCommands = value};
    }
    public IReadOnlyList<(int ThreadId, string Text)> FixCommandReplies
    {
        get => AutoFix.FixCommandReplies;
        set => AutoFix = AutoFix with {FixCommandReplies = value};
    }

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
        Repository.Dispose();
        RepoLease?.Dispose();
        RepoLease = null;
    }

    public string RequireRepoDir()
    {
        if (Repository.RepoDir is not { } dir)
        {
            throw new InvalidOperationException(
                $"stage ordering violation: {nameof(RepoDir)} is null but required");
        }

        return dir;
    }

    public PullRequest RequirePullRequest()
        => Fetch.PullRequest ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(PullRequest)} is null but required (fetch stage must run first)");

    public ReviewResult RequireResult()
        => Reasoning.Result ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(Result)} is null but required (reasoning stage must run first)");

    public ResolveState RequireResolveState()
        => Resolve ?? throw new InvalidOperationException("resolve state is not initialized for this run");

    public void Terminate(string reason)
    {
        Terminated = true;
        TerminationReason = reason;
    }
}
