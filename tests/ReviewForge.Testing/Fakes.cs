using Microsoft.Extensions.AI;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Testing;

/// <summary>In-memory IPullRequestSource recording every write.</summary>
public class FakePullRequestSource : IPullRequestSource
{
    private readonly object _Gate = new();
    private int _NextThreadId = 1000;
    private int _OpenPullRequestsFetches;
    private int _WorkItemFetches;
    private int _ThreadFetches;
    public List<PullRequestCandidate> OpenPullRequests { get; set; } = [];
    public int OpenPullRequestsFetches => _OpenPullRequestsFetches;
    public int WorkItemFetches => _WorkItemFetches;

    public PullRequest Pr { get; set; } = new(
        1, "title", "desc", "head-sha", "base-sha", "https://clone", IsDraft: false,
        CreatorId: "creator-1", CreatorName: "PR Author", SourceRefName: "refs/heads/feature/test");

    public Dictionary<PrKey, PullRequest> PullRequestsByKey { get; } = [];
    public List<WorkItem> WorkItems { get; set; } = [];
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public List<ReviewThread> Threads { get; set; } = [];
    public CurrentUser User { get; set; } = new("user-1", "reviewforge bot");
    public int ThreadFetches => _ThreadFetches;

    /// <summary>Optional barrier armed to prove concurrency in discovery tests; trips when
    /// <see cref="WorkItems"/> is awaited by that many concurrent callers.</summary>
    public Barrier? WorkItemBarrier { get; set; }

    public List<(RichFinding Finding, int ThreadId)> PostedFindings { get; } = [];
    public List<string> PostedFindingBodies { get; } = [];
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

    /// <summary>Optional exception for PR retrieval tests.</summary>
    public Exception? ThrowOnGetPullRequest { get; set; }

    /// <summary>Optional exception for discovery fetch tests.</summary>
    public Exception? ThrowOnGetOpenPullRequests { get; set; }

    /// <summary>Optional exception for linked work-item fetch tests.</summary>
    public Exception? ThrowOnGetLinkedWorkItems { get; set; }

    public virtual Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
    {
        if (ThrowOnGetPullRequest is { } error)
        {
            return Task.FromException<PullRequest>(error);
        }

        return Task.FromResult(PullRequestsByKey.TryGetValue(pr, out var pullRequest) ? pullRequest : Pr);
    }

    public virtual Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _OpenPullRequestsFetches);
        if (ThrowOnGetOpenPullRequests is { } error)
        {
            return Task.FromException<IReadOnlyList<PullRequestCandidate>>(error);
        }

        return Task.FromResult<IReadOnlyList<PullRequestCandidate>>(OpenPullRequests);
    }

    public virtual async Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _WorkItemFetches);
        if (ThrowOnGetLinkedWorkItems is { } error)
        {
            throw error;
        }

        if (WorkItemBarrier is not null && !WorkItemBarrier.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("work-item fetches did not overlap");
        }

        return WorkItems;
    }

    public virtual Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChangedFile>>(ChangedFiles);

    public virtual Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _ThreadFetches);
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
            PostedFindingBodies.Add(CommentFormatter.FormatFinding(finding));
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
    private int _LastRunFetches;
    public int LastRunFetches => _LastRunFetches;
    public List<string> KnownKeys { get; set; } = [];
    public List<(Guid RunId, string Key, int ThreadId)> ThreadIdBackfills { get; } = [];
    public List<ReviewRun> RecentRuns { get; } = [];

    /// <summary>Optional exception for SaveRunAsync failure tests.</summary>
    public Exception? ThrowOnSave { get; set; }

    /// <summary>Optional exception for PingAsync health-check tests.</summary>
    public Exception? ThrowOnPing { get; set; }


    public virtual Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct)
    {
        Interlocked.Increment(ref _LastRunFetches);
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

    public virtual Task<IReadOnlySet<long>> GetCommandedFixThreadIdsAsync(PrKey pr, CancellationToken ct)
        => Task.FromResult<IReadOnlySet<long>>(
            Runs.Where(r => r.Pr == pr)
                .SelectMany(r => r.Findings)
                .Where(f => f.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal))
                .Select(f => f.DedupeKey[AppliedFix.CommandKeyPrefix.Length..])
                .Where(s => long.TryParse(s, out _))
                .Select(long.Parse)
                .ToHashSet());

    public virtual Task SaveRunAsync(ReviewRun run, CancellationToken ct)
    {
        if (ThrowOnSave is { } error)
        {
            return Task.FromException(error);
        }

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

    public virtual Task MarkFindingPublishedAsync(Guid runId, string dedupeKey, int? threadId, CancellationToken ct)
    {
        var run = Runs.FirstOrDefault(r => r.Id == runId);
        if (run is not null)
        {
            var findings = run.Findings.Select(f => f.DedupeKey == dedupeKey
                ? f with {Published = true, ThreadId = threadId ?? f.ThreadId}
                : f).ToArray();
            Runs[Runs.IndexOf(run)] = run with {Findings = findings};
        }

        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReviewRun>>(
            [.. RecentRuns.Where(r => r.Pr == pr).OrderByDescending(r => r.StartedAt).Take(count)]);

    public virtual Task<ReviewRun?> GetRunAsync(Guid runId, CancellationToken ct)
        => Task.FromResult(Runs.FirstOrDefault(r => r.Id == runId));

    public virtual Task PingAsync(CancellationToken ct)
        => ThrowOnPing is { } error ? Task.FromException(error) : Task.CompletedTask;

    public List<ResolveAction> ResolveActions { get; } = [];
    private int _NextResolveActionId = 1;
    private readonly Dictionary<int, PrKey> _ResolveActionPrs = [];

    public virtual Task<ReviewRun?> GetLastCompletedResolveRunAsync(PrKey pr, CancellationToken ct)
        => Task.FromResult(Runs.Where(r => r.Pr == pr && r.Pipeline == RunKind.Resolve.ToString()
                                                      && r.CompletedAt is not null && r.Success)
            .OrderByDescending(r => r.CompletedAt).FirstOrDefault());

    public virtual Task<IReadOnlyList<ResolveAction>> GetResolveActionsAsync(
        PrKey pr, IReadOnlyCollection<int> threadIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ResolveAction>>(
        [
            .. ResolveActions.Where(a => _ResolveActionPrs.TryGetValue(a.Id, out var key) && key == pr)
                .Where(a => threadIds.Count == 0 || threadIds.Contains(a.ThreadId))
        ]);

    public virtual Task SaveResolveActionsAsync(
        PrKey pr, Guid runId, IReadOnlyList<ResolveAction> actions, CancellationToken ct)
    {
        foreach (var action in actions)
        {
            var index = ResolveActions.FindIndex(a =>
                _ResolveActionPrs.TryGetValue(a.Id, out var key) && key == pr && a.ThreadId == action.ThreadId);
            var saved = action with
            {
                Id = index >= 0 ? ResolveActions[index].Id : _NextResolveActionId++,
                RunId = runId, ReplyPosted = false
            };
            if (index >= 0) ResolveActions[index] = saved;
            else ResolveActions.Add(saved);
            _ResolveActionPrs[saved.Id] = pr;
        }

        return Task.CompletedTask;
    }

    public virtual Task MarkResolveActionRepliedAsync(int id, CancellationToken ct)
    {
        var index = ResolveActions.FindIndex(a => a.Id == id);
        if (index >= 0) ResolveActions[index] = ResolveActions[index] with {ReplyPosted = true};
        return Task.CompletedTask;
    }

    // Resolve action persistence is kept in-memory for pipeline tests.

    /// <summary>Pushed-fix rows saved via <see cref="SavePushedFixesAsync"/> (Id assigned
    /// sequentially from 1, Pushed=false intent); <see cref="ConfirmPushedFixesAsync"/> flips
    /// Pushed, <see cref="AbandonPushedFixesAsync"/> drops unconfirmed rows of the run, and
    /// <see cref="MarkPushedFixRepliedAsync"/> flips ReplyPosted.</summary>
    public List<PushedFix> PushedFixes { get; } = [];

    private int _NextPushedFixId = 1;

    public virtual Task SavePushedFixesAsync(PrKey pr, Guid runId, IReadOnlyList<PushedFix> fixes, CancellationToken ct)
    {
        foreach (var fix in fixes)
        {
            PushedFixes.Add(fix with {Id = _NextPushedFixId++, RunId = runId, Pushed = false, ReplyPosted = false});
        }

        return Task.CompletedTask;
    }

    public virtual Task ConfirmPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct)
    {
        for (var i = 0; i < PushedFixes.Count; i++)
        {
            if (PushedFixes[i].RunId == runId && !PushedFixes[i].Pushed)
            {
                PushedFixes[i] = PushedFixes[i] with {Pushed = true};
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task AbandonPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct)
    {
        PushedFixes.RemoveAll(f => f.RunId == runId && !f.Pushed);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<PushedFix>> GetUnrepliedPushedFixesAsync(PrKey pr, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PushedFix>>([.. PushedFixes.Where(f => f.Pushed && !f.ReplyPosted)]);

    public virtual Task MarkPushedFixRepliedAsync(int pushedFixId, CancellationToken ct)
    {
        var index = PushedFixes.FindIndex(f => f.Id == pushedFixId);
        if (index >= 0)
        {
            PushedFixes[index] = PushedFixes[index] with {ReplyPosted = true};
        }

        return Task.CompletedTask;
    }
}

/// <summary>Fake git: serves a scripted diff, records checkouts.</summary>
public class FakeGitOps : IGitOps
{
    private readonly object _Gate = new();
    private int _ActiveClones;
    private int _MaxConcurrentClones;
    public string Diff { get; set; } = string.Empty;
    public string MergeBaseSha { get; set; } = "fake-merge-base";
    public string RepoDir { get; set; } = Path.Combine(Path.GetTempPath(), "reviewforge-fake-repo");
    public List<string> Checkouts { get; } = [];
    public List<(string Base, string Head)> EnsuredCommits { get; } = [];
    public List<(string Base, string Head)> DiffRequests { get; } = [];
    public List<(string First, string Second)> MergeBaseRequests { get; } = [];

    /// <summary>Optional exception for diff retrieval tests.</summary>
    public Exception? ThrowOnGetDiff { get; set; }

    public List<(string MirrorPath, string Base, string Head)> Warmups { get; } = [];
    public TimeSpan CloneDelay { get; set; }
    public int MaxConcurrentClones => _MaxConcurrentClones;

    // ---- Write surface (CommitOnHead) ----

    /// <summary>Recorded commits: message + staged paths (null = whole worktree).</summary>
    public List<(string Message, IReadOnlyList<string>? Paths)> Commits { get; } = [];

    /// <summary>Recorded pushes: branch + expected tip the caller pinned.</summary>
    public List<(string Branch, string ExpectedTip)> Pushes { get; } = [];

    /// <summary>Mutable remote tip read by <see cref="GetRemoteTipAsync"/> and validated by
    /// <see cref="PushAsync"/>; tests flip it to trigger the pin failure.</summary>
    public string? RemoteTip { get; set; } = "head-sha";

    /// <summary>Commit info served by <see cref="GetCommitInfoAsync"/> (loop-guard tests).</summary>
    public TipCommitInfo? HeadInfo { get; set; }

    /// <summary>Optional exception for commit failure tests.</summary>
    public Exception? ThrowOnCommit { get; set; }

    /// <summary>Optional exception for push failure tests.</summary>
    public Exception? ThrowOnPush { get; set; }

    private int _CommitCount;

    public virtual Task<string> CommitAsync(
        string repoPath, string message, string authorName, string authorEmail,
        IReadOnlyList<string>? paths, CancellationToken ct)
    {
        if (ThrowOnCommit is { } error)
        {
            return Task.FromException<string>(error);
        }

        lock (_Gate)
        {
            Commits.Add((message, paths));
            return Task.FromResult($"fake-{++_CommitCount:D40}");
        }
    }

    public virtual Task PushAsync(
        string repoPath, string cloneUrl, string remoteBranch, string expectedRemoteTipSha, string? pat, CancellationToken ct)
    {
        if (ThrowOnPush is { } error)
        {
            return Task.FromException(error);
        }

        lock (_Gate)
        {
            if (!string.Equals(RemoteTip, expectedRemoteTipSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new PrHeadChangedException(
                    expectedRemoteTipSha, RemoteTip ?? "(branch missing on remote)");
            }

            Pushes.Add((remoteBranch, expectedRemoteTipSha));
        }

        return Task.CompletedTask;
    }

    public virtual Task<string?> GetRemoteTipAsync(
        string repoPath, string cloneUrl, string remoteBranch, string? pat, CancellationToken ct)
        => Task.FromResult(RemoteTip);

    public virtual Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct)
        => Task.FromResult(HeadInfo);

    public virtual async Task<string> CloneOrOpenAsync(
        string cloneUrl, string workDir, string? pat, CancellationToken ct, string? mirrorPath = null)
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

    public virtual Task WarmupMirrorAsync(string mirrorPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct)
    {
        lock (_Gate)
        {
            Warmups.Add((mirrorPath, baseSha, headSha));
        }

        return Task.CompletedTask;
    }

    public virtual Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
    {
        DiffRequests.Add((baseSha, headSha));
        return ThrowOnGetDiff is { } error ? Task.FromException<string>(error) : Task.FromResult(Diff);
    }

    public virtual Task<string> GetMergeBaseShaAsync(string repoPath, string firstSha, string secondSha, CancellationToken ct)
    {
        MergeBaseRequests.Add((firstSha, secondSha));
        return Task.FromResult(MergeBaseSha);
    }
}

/// <summary>Fake enricher: fixed payload, null, or throwing.</summary>
public class FakeEnricher(string? payload = null, bool throws = false) : IContextEnricher
{
    // Preserve the historical CRG staging key for this legacy test double; new enrichers
    // use their own explicit Name.
    public string Name => "crg";
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

/// <summary>Factory handing out one scripted chat client. Records the tiers requested via
/// <see cref="Create(ChatTier)"/> so routing tests can assert which model a run used.</summary>
public class FakeChatClientFactory(IChatClient client, string model = "test-model", string? fastModel = null) : IChatClientFactory
{
    /// <summary>Tier sequence passed to <see cref="Create(ChatTier)"/>, in call order.</summary>
    public List<ChatTier> RequestedTiers { get; } = [];

    public string ModelName(ChatTier tier)
        => tier == ChatTier.Fast && fastModel is not null ? fastModel : model;

    public IChatClient Create(ChatTier tier)
    {
        RequestedTiers.Add(tier);
        return client;
    }

    public bool IsTransientFailure(Exception exception)
        => exception is HttpRequestException {StatusCode: null} or TimeoutException;
}

/// <summary>In-memory <see cref="IWorkspaceFs"/> backed by the real filesystem (temp dirs).</summary>
public sealed class FakeWorkspaceFs : IWorkspaceFs
{
    /// <summary>Optional exception for recursive size enumeration tests.</summary>
    public Exception? ThrowOnEnumerateFilesRecursive { get; set; }

    /// <summary>Optional exception for delete tests.</summary>
    public Exception? ThrowOnDeleteDirectory { get; set; }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path).ToArray();

    public string[] EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path).ToArray();

    public string[] EnumerateFilesRecursive(string path)
        => ThrowOnEnumerateFilesRecursive is { } error
            ? throw error
            : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public DateTime GetCreationTimeUtc(string path) => Directory.GetCreationTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime timestamp) => Directory.SetLastWriteTimeUtc(path, timestamp);

    public async Task<IDisposable> AcquireExclusiveLockAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    public IDisposable? TryAcquireExclusiveLock(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 11 or 32 or 33)
        {
            return null;
        }
    }


    public void DeleteDirectory(string path, bool recursive)
    {
        if (ThrowOnDeleteDirectory is { } error)
        {
            throw error;
        }

        Directory.Delete(path, recursive);
    }
}