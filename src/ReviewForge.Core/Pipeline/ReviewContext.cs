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

    /// <summary>DedupeKeys whose live ADO thread is Fixed/Closed in the stage-1 snapshot.
    /// A verbatim re-submission of one of these resurfaces as a regression (P1-11) instead
    /// of staying dedupe-silent; keys with Active/Pending threads stay in the known set only.</summary>
    public IReadOnlySet<string> ResolvedKeys { get; set; } = new HashSet<string>(StringComparer.Ordinal);

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

    /// <summary>Threads refresh that stage 3 starts before the clone so stage 4 can consume
    /// the in-flight fetch instead of paying the round-trip serially. Stage 4 consumes it
    /// only when the response was received at/after <see cref="RepoPreparedAt"/> — see
    /// <see cref="ThreadsRefreshOverlap.CompletedAt"/>.</summary>
    public ThreadsRefreshOverlap? PendingThreadsRefresh { get; set; }

    /// <summary>Wall-clock stamp for when repository preparation finished; bounds the stage-4
    /// freshness predicate on <see cref="PendingThreadsRefresh"/>.</summary>
    public DateTimeOffset? RepoPreparedAt { get; set; }

    /// <summary>Head-commit author/message read from the local checkout by stage 3 (loop-guard
    /// input); null when the commit info could not be read — the guard treats that as "proceed".</summary>
    public TipCommitInfo? HeadCommitInfo { get; set; }

    /// <summary>Enrichment call stage 3 starts once RepoDir+DiffText exist; stage 5 awaits it
    /// with the same fail-safe handling as a call it made itself.</summary>
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

    // Stage 7.2 — auto-fix: suggestion fixes that passed all gates (deterministic + commanded)
    public IReadOnlyList<AutoFix.AppliedFix> AppliedFixes { get; set; } = [];

    /// <summary>Set by stage 7.7 after a successful CommitOnHead push: the new PR head created
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
        if (RepoDir is not { } dir)
        {
            throw new InvalidOperationException(
                $"stage ordering violation: {nameof(RepoDir)} is null but required");
        }

        return dir;
    }

    public PullRequest RequirePullRequest()
        => PullRequest ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(PullRequest)} is null but required (fetch stage must run first)");

    public ReviewResult RequireResult()
        => Result ?? throw new InvalidOperationException(
            $"stage ordering violation: {nameof(Result)} is null but required (reasoning stage must run first)");

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