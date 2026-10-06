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

    /// <summary>Threads refresh overlap remains on the context until phase 3 relocates its protocol.</summary>
    public ThreadsRefreshOverlap? PendingThreadsRefresh { get; set; }
    public DateTimeOffset? RepoPreparedAt { get; set; }

    /// <summary>Head-commit author/message read by stage 3; null when unavailable.</summary>
    public TipCommitInfo? HeadCommitInfo
    {
        get => Repository.HeadCommitInfo;
        set => Repository = Repository with {HeadCommitInfo = value};
    }

    /// <summary>Stage 3 starts enrichment when repository outputs are final; stage 5 awaits it.</summary>
    public Task<string?>? PendingEnrichment { get; set; }

    // Stage 4 — classify
    public ReviewKind Kind { get; set; } = ReviewKind.Full;
    public IReadOnlyList<PendingReply> PendingReplies { get; set; } = [];

    // Stage 5/6 — reasoning
    public ContextStore ContextStore { get; } = new();
    public ReviewCollector Collector { get; set; } = new();
    public ReviewResult? Result { get; set; }

    // Stage 7 — validation output: findings accepted for posting
    public IReadOnlyList<RichFinding> AcceptedFindings { get; set; } = [];

    // Stage 8 — auto-fix: suggestion fixes that passed all gates (deterministic + commanded)
    public IReadOnlyList<AutoFix.AppliedFix> AppliedFixes { get; set; } = [];

    /// <summary>Set by stage 10 after a successful CommitOnHead push: the new PR head created
    /// by THIS run. Publish's head-unchanged check accepts it as the expected head — the run's
    /// own push is the one legal head movement; anything beyond it still fails the run.</summary>
    public string? PushedHeadSha { get; set; }
    public IReadOnlyList<AutoFix.FixCommand> FixCommands { get; set; } = [];

    /// <summary>Replies queued by the auto-fix stage, posted by the publish stage (declines,
    /// verifier failures, exhausted budget, links to posted suggestions).</summary>
    public IReadOnlyList<(int ThreadId, string Text)> FixCommandReplies { get; set; } = [];

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
        // Stage 3 may still have the threads refresh or the enrichment call in flight when a
        // later stage fails: cancel them so neither keeps running against a released
        // checkout, and observe any late fault so it never surfaces as an unobserved-task
        // exception. By disposal time no consumer cares about the results anymore.
        OverlapCts.Cancel();
        ObserveFault(PendingThreadsRefresh?.Task);
        ObserveFault(PendingEnrichment);
        RepoLease?.Dispose();
        RepoLease = null;
    }

    /// <summary>Cancels stage-3 overlap work (threads refresh, enrichment) on disposal so a
    /// failed or reaped run cannot leave fetches running against a released checkout.</summary>
    internal CancellationTokenSource OverlapCts => field ??= new();

    private static void ObserveFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        => Result ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(Result)} is null but required (reasoning stage must run first)");

    public ResolveState RequireResolveState()
        => Resolve ?? throw new InvalidOperationException("resolve state is not initialized for this run");

    public void Terminate(string reason)
    {
        Terminated = true;
        TerminationReason = reason;
    }
}

/// <summary>
/// Stage-3 threads-refresh overlap: the fetch task plus the stamp of when it was launched.
/// <see cref="CompletedAt"/> is stamped by the completion continuation stage 3 attaches at
/// kickoff; stage 4 consumes the result only when that stamp is at/after preparation time
/// (a response received earlier predates the clone window and missed comments that arrived
/// during it, so it takes the serial refetch exactly like the pre-overlap pipeline).
/// </summary>
public sealed record ThreadsRefreshOverlap(
    Task<IReadOnlyList<ReviewThread>> Task,
    DateTimeOffset StartedAt)
{
    /// <summary>Utc stamp of the received response; null while genuinely in flight. Stage 4
    /// conservatively treats "already complete but unstamped" as unproven and re-fetches.</summary>
    public DateTimeOffset? CompletedAt { get; internal set; }
}