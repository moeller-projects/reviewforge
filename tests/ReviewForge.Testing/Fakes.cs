using Microsoft.Extensions.AI;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Testing;

/// <summary>In-memory IPullRequestSource recording every write.</summary>
public class FakePullRequestSource : IPullRequestSource
{
    private readonly object _Gate = new();
    private int _NextThreadId = 1000;
    private int _openPullRequestsFetches;
    private int _workItemFetches;
    private int _threadFetches;
    public List<PullRequestCandidate> OpenPullRequests { get; set; } = [];
    public int OpenPullRequestsFetches => _openPullRequestsFetches;
    public int WorkItemFetches => _workItemFetches;
    public PullRequest Pr { get; set; } = new(
        1, "title", "desc", "head-sha", "base-sha", "https://clone", IsDraft: false,
        CreatorId: "creator-1", CreatorName: "PR Author");
    public Dictionary<PrKey, PullRequest> PullRequestsByKey { get; } = [];
    public List<WorkItem> WorkItems { get; set; } = [];
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public List<ReviewThread> Threads { get; set; } = [];
    public CurrentUser User { get; set; } = new("user-1", "reviewforge bot");
    public int ThreadFetches => _threadFetches;

    /// <summary>Optional barrier armed to prove concurrency in discovery tests; trips when
    /// <paramref name="WorkItems"/> is awaited by that many concurrent callers.</summary>
    public Barrier? WorkItemBarrier { get; set; }

    public List<(RichFinding Finding, int ThreadId)> PostedFindings { get; } = [];
    public List<(ThreadAnchor Anchor, string Body, int ThreadId)> PostedSuggestions { get; } = [];
    public List<string?> PostedSuggestionDedupeKeys { get; } = [];
    public List<string> GeneralComments { get; } = [];
    public List<string?> GeneralCommentDedupeKeys { get; } = [];
    public List<(int ThreadId, string Text)> Replies { get; } = [];
    public List<(int ThreadId, ReviewThreadStatus Status)> StatusChanges { get; } = [];
    public List<(string ReviewerId, ReviewerVote Vote)> Votes { get; } = [];

    /// <summary>When set, PostFindingThreadAsync throws for a finding with this dedupe key
    /// (deterministic partial-failure tests).</summary>
    public string? ThrowOnPostKey { get; set; }

    /// <summary>When set, the Nth PostFindingThreadAsync call throws (order-agnostic partial failure).</summary>
    public int? ThrowOnNthPost { get; set; }
    private int _PostCount;

    public virtual Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        => Task.FromResult(PullRequestsByKey.TryGetValue(pr, out var pullRequest) ? pullRequest : Pr);

    public virtual Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _openPullRequestsFetches);
        return Task.FromResult<IReadOnlyList<PullRequestCandidate>>(OpenPullRequests);
    }

    public virtual async Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _workItemFetches);
        if (WorkItemBarrier is not null && !WorkItemBarrier.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("work-item fetches did not overlap");
        }

        return WorkItems;
    }

    public virtual Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChangedFile>>(ChangedFiles);

    public virtual Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _threadFetches);
        return Task.FromResult<IReadOnlyList<ReviewThread>>(Threads);
    }

    public virtual Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(User);

    public virtual Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct)
    {
        lock (_Gate)
        {
            if (ThrowOnNthPost is { } nth && ++_PostCount == nth)
            {
                throw new InvalidOperationException($"post #{nth} failed");
            }

            if (ThrowOnPostKey is not null && finding.DedupeKey == ThrowOnPostKey)
            {
                throw new InvalidOperationException($"post of {finding.DedupeKey} failed");
            }

            var id = _NextThreadId++;
            PostedFindings.Add((finding, id));
            return Task.FromResult(id);
        }
    }

    public virtual Task<int> PostSuggestionThreadAsync(PrKey pr, ThreadAnchor anchor, string body, CancellationToken ct)
    {
        lock (_Gate)
        {
            var id = _NextThreadId++;
            PostedSuggestions.Add((anchor, body, id));
            PostedSuggestionDedupeKeys.Add(null); // structural invariant: never a dedupe key
            return Task.FromResult(id);
        }
    }

    public virtual Task PostGeneralCommentAsync(
        PrKey pr,
        string text,
        string? dedupeKey,
        CancellationToken ct)
    {
        lock (_Gate)
        {
            GeneralComments.Add(text);
            GeneralCommentDedupeKeys.Add(dedupeKey);
        }

        return Task.CompletedTask;
    }

    public virtual Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct)
    {
        lock (_Gate)
        {
            Replies.Add((threadId, text));
        }

        return Task.CompletedTask;
    }

    public virtual Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct)
    {
        lock (_Gate)
        {
            StatusChanges.Add((threadId, status));
        }

        return Task.CompletedTask;
    }

    public virtual Task SetReviewerVoteAsync(PrKey pr, string reviewerId, ReviewerVote vote, CancellationToken ct)
    {
        lock (_Gate)
        {
            Votes.Add((reviewerId, vote));
        }

        return Task.CompletedTask;
    }
}

/// <summary>FakePullRequestSource with a per-write delay and a global write-order log.</summary>
public class SlowFakePullRequestSource(int delayMs = 0) : FakePullRequestSource
{
    private readonly object _LogGate = new();
    private int _ActiveFindingPosts;
    private int _CommentCount;
    public List<string> WriteLog { get; } = [];
    public int MaxConcurrentFindingPosts { get; private set; }

    private void BeginFindingPost()
    {
        lock (_LogGate)
        {
            _ActiveFindingPosts++;
            MaxConcurrentFindingPosts = Math.Max(MaxConcurrentFindingPosts, _ActiveFindingPosts);
        }
    }

    private void EndFindingPost()
    {
        lock (_LogGate)
        {
            _ActiveFindingPosts--;
        }
    }

    private async Task DelayAsync(CancellationToken ct)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    public override async Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct)
    {
        BeginFindingPost();
        try
        {
            await DelayAsync(ct);
            var threadId = await base.PostFindingThreadAsync(pr, finding, ct);
            lock (_LogGate)
            {
                WriteLog.Add($"finding {finding.DedupeKey}");
            }

            return threadId;
        }
        finally
        {
            EndFindingPost();
        }
    }

    public override async Task PostGeneralCommentAsync(
        PrKey pr,
        string text,
        string? dedupeKey,
        CancellationToken ct)
    {
        await DelayAsync(ct);
        await base.PostGeneralCommentAsync(pr, text, dedupeKey, ct);
        lock (_LogGate)
        {
            WriteLog.Add($"comment {_CommentCount++}");
        }
    }

    public override async Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct)
    {
        await DelayAsync(ct);
        await base.ReplyToThreadAsync(pr, threadId, text, ct);
    }

    public override async Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct)
    {
        await DelayAsync(ct);
        await base.SetThreadStatusAsync(pr, threadId, status, ct);
    }

    public override async Task SetReviewerVoteAsync(PrKey pr, string reviewerId, ReviewerVote vote, CancellationToken ct)
    {
        await DelayAsync(ct);
        await base.SetReviewerVoteAsync(pr, reviewerId, vote, ct);
    }
}

/// <summary>In-memory IFindingStore.</summary>
public class FakeFindingStore : IFindingStore
{
    public List<ReviewRun> Runs { get; } = [];
    public PriorRun? LastRun { get; set; }
    private int _lastRunFetches;
    public int LastRunFetches => _lastRunFetches;
    public List<string> KnownKeys { get; set; } = [];
    public List<(Guid RunId, string Key, int ThreadId)> ThreadIdBackfills { get; } = [];
    public List<ReviewRun> RecentRuns { get; } = [];

    public virtual Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _lastRunFetches);
        return Task.FromResult(LastRun);
    }

    public virtual Task<IReadOnlyList<ReviewRun>> GetStaleShellsAsync(DateTimeOffset olderThan, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReviewRun>>(
            [.. Runs.Where(r => r.CompletedAt is null && r.StartedAt < olderThan)]);

    public List<(DateTimeOffset OlderThan, int MinRunsPerPr)> PruneCalls { get; } = [];

    public virtual Task<int> PruneAsync(DateTimeOffset olderThan, int minRunsPerPr, CancellationToken ct)
    {
        PruneCalls.Add((olderThan, minRunsPerPr));
        return Task.FromResult(0);
    }

    public virtual Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(KnownKeys);

    public virtual Task SaveRunAsync(ReviewRun run, CancellationToken ct)
    {
        Runs.RemoveAll(r => r.Id == run.Id);
        Runs.Add(run);
        RecentRuns.RemoveAll(r => r.Id == run.Id);
        RecentRuns.Add(run);
        return Task.CompletedTask;
    }

    public virtual Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct)
    {
        ThreadIdBackfills.Add((runId, dedupeKey, threadId));
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReviewRun>>(
            [.. RecentRuns.Where(r => r.Pr == pr).OrderByDescending(r => r.StartedAt).Take(count)]);

    public virtual Task PingAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Fake git: serves a scripted diff, records checkouts.</summary>
public class FakeGitOps : IGitOps
{
    private readonly object _Gate = new();
    private int _ActiveClones;
    private int _MaxConcurrentClones;
    public string Diff { get; set; } = string.Empty;
    public string RepoDir { get; set; } = Path.Combine(Path.GetTempPath(), "reviewforge-fake-repo");
    public List<string> Checkouts { get; } = [];
    public List<(string Base, string Head)> EnsuredCommits { get; } = [];
    public TimeSpan CloneDelay { get; set; }
    public int MaxConcurrentClones => _MaxConcurrentClones;

    public virtual async Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var active = Interlocked.Increment(ref _ActiveClones);
        while (true)
        {
            var observed = Volatile.Read(ref _MaxConcurrentClones);
            if (active <= observed || Interlocked.CompareExchange(ref _MaxConcurrentClones, active, observed) == observed)
            {
                break;
            }
        }

        if (CloneDelay > TimeSpan.Zero)
        {
            await Task.Delay(CloneDelay, ct).ConfigureAwait(false);
        }

        Interlocked.Decrement(ref _ActiveClones);
        return workDir;
    }

    public virtual Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct)
    {
        lock (_Gate)
        {
            Checkouts.Add(commitSha);
        }

        return Task.CompletedTask;
    }

    public virtual Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct) => Task.FromResult<string?>(null);

    public virtual Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct)
    {
        lock (_Gate)
        {
            EnsuredCommits.Add((baseSha, headSha));
        }

        return Task.CompletedTask;
    }

    public virtual Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null) => Task.FromResult(Diff);
}

/// <summary>Fake enricher: fixed payload, null, or throwing.</summary>
public class FakeEnricher(string? payload = null, bool throws = false) : IContextEnricher
{
    public int Calls { get; private set; }

    public Task<string?> EnrichAsync(string repoDir, string diffText, CancellationToken ct)
    {
        Calls++;
        if (throws)
        {
            throw new InvalidOperationException("enricher exploded");
        }

        return Task.FromResult(payload);
    }
}

/// <summary>Factory handing out one scripted chat client.</summary>
public class FakeChatClientFactory(IChatClient client, string model = "test-model") : IChatClientFactory
{
    public string ModelName => model;
    public IChatClient Create() => client;
}

/// <summary>In-memory <see cref="IWorkspaceFs"/> backed by the real filesystem (temp dirs).</summary>
public sealed class FakeWorkspaceFs : IWorkspaceFs
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path).ToArray();

    public string[] EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path).ToArray();

    public string[] EnumerateFilesRecursive(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime timestamp) => Directory.SetLastWriteTimeUtc(path, timestamp);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}