using Microsoft.Extensions.AI;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Testing;

/// <summary>In-memory IPullRequestSource recording every write.</summary>
public class FakePullRequestSource : IPullRequestSource
{
    private int _NextThreadId = 1000;
    public List<PullRequestCandidate> OpenPullRequests { get; set; } = [];
    public int OpenPullRequestsFetches { get; private set; }
    public int WorkItemFetches { get; private set; }
    public PullRequest Pr { get; set; } = new(1, "title", "desc", "head-sha", "base-sha", "https://clone", IsDraft: false);
    public Dictionary<PrKey, PullRequest> PullRequestsByKey { get; } = [];
    public List<WorkItem> WorkItems { get; set; } = [];
    public List<ChangedFile> ChangedFiles { get; set; } = [];
    public List<ReviewThread> Threads { get; set; } = [];
    public CurrentUser User { get; set; } = new("user-1", "reviewforge bot");
    public int ThreadFetches { get; private set; }

    public List<(RichFinding Finding, int ThreadId)> PostedFindings { get; } = [];
    public List<string> GeneralComments { get; } = [];
    public List<(int ThreadId, string Text)> Replies { get; } = [];
    public List<(int ThreadId, ReviewThreadStatus Status)> StatusChanges { get; } = [];
    public List<(string ReviewerId, int Vote)> Votes { get; } = [];

    public virtual Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
        => Task.FromResult(PullRequestsByKey.TryGetValue(pr, out var pullRequest) ? pullRequest : Pr);

    public virtual Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
    {
        OpenPullRequestsFetches++;
        return Task.FromResult<IReadOnlyList<PullRequestCandidate>>(OpenPullRequests);
    }

    public virtual Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(PrKey pr, CancellationToken ct)
    {
        WorkItemFetches++;
        return Task.FromResult<IReadOnlyList<WorkItem>>(WorkItems);
    }

    public virtual Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<ChangedFile>>(ChangedFiles);

    public virtual Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
    {
        ThreadFetches++;
        return Task.FromResult<IReadOnlyList<ReviewThread>>(Threads);
    }

    public virtual Task<CurrentUser> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult(User);

    public virtual Task<int> PostFindingThreadAsync(PrKey pr, RichFinding finding, CancellationToken ct)
    {
        var id = _NextThreadId++;
        PostedFindings.Add((finding, id));
        return Task.FromResult(id);
    }

    public virtual Task PostGeneralCommentAsync(PrKey pr, string text, CancellationToken ct)
    {
        GeneralComments.Add(text);
        return Task.CompletedTask;
    }

    public virtual Task ReplyToThreadAsync(PrKey pr, int threadId, string text, CancellationToken ct)
    {
        Replies.Add((threadId, text));
        return Task.CompletedTask;
    }

    public virtual Task SetThreadStatusAsync(PrKey pr, int threadId, ReviewThreadStatus status, CancellationToken ct)
    {
        StatusChanges.Add((threadId, status));
        return Task.CompletedTask;
    }

    public virtual Task SetReviewerVoteAsync(PrKey pr, string reviewerId, int vote, CancellationToken ct)
    {
        Votes.Add((reviewerId, vote));
        return Task.CompletedTask;
    }
}

/// <summary>In-memory IFindingStore.</summary>
public class FakeFindingStore : IFindingStore
{
    public List<ReviewRun> Runs { get; } = [];
    public PriorRun? LastRun { get; set; }
    public int LastRunFetches { get; private set; }
    public List<string> KnownKeys { get; set; } = [];
    public List<(Guid RunId, string Key, int ThreadId)> ThreadIdBackfills { get; } = [];

    public virtual Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct)
    {
        LastRunFetches++;
        return Task.FromResult(LastRun);
    }

    public virtual Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(KnownKeys);

    public virtual Task SaveRunAsync(ReviewRun run, CancellationToken ct)
    {
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public virtual Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct)
    {
        ThreadIdBackfills.Add((runId, dedupeKey, threadId));
        return Task.CompletedTask;
    }
}

/// <summary>Fake git: serves a scripted diff, records checkouts.</summary>
public class FakeGitOps : IGitOps
{
    public string Diff { get; set; } = string.Empty;
    public string RepoDir { get; set; } = Path.Combine(Path.GetTempPath(), "reviewforge-fake-repo");
    public List<string> Checkouts { get; } = [];
    public TimeSpan CloneDelay { get; set; }
    public int MaxConcurrentClones => _MaxConcurrentClones;
    private int _ActiveClones;
    private int _MaxConcurrentClones;

    public virtual string CloneOrOpen(string cloneUrl, string workDir, string? pat)
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
            Thread.Sleep(CloneDelay);
        }

        Interlocked.Decrement(ref _ActiveClones);
        return workDir;
    }

    public virtual void Checkout(string repoPath, string commitSha) => Checkouts.Add(commitSha);
    public virtual string? GetHeadSha(string repoPath) => null;
    public virtual void FetchCommits(string repoPath, string? pat, IReadOnlyList<string> refSpecs) { }
    public virtual string GetDiff(string repoPath, string baseSha, string headSha) => Diff;
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