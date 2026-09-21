using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class RepoCheckoutPoolTests : IDisposable
{
    private readonly string _Root = Path.Combine(Path.GetTempPath(), "reviewforge-pool-" + Guid.NewGuid().ToString("N"));

    public RepoCheckoutPoolTests() => Directory.CreateDirectory(_Root);

    public void Dispose()
    {
        if (Directory.Exists(_Root))
        {
            Directory.Delete(_Root, recursive: true);
        }
    }

    private RepoCheckoutPool Pool(TestGitOps git, string? pat = null)
        => new(git, new FakeWorkspaceFs(), _Root, pat);

    [Fact]
    public void Sanitizes_path_components_without_separator_collisions()
    {
        Assert.Equal("repo_id", RepoCheckoutPool.Sanitize("repo/id"));
        Assert.Equal("repo-id", RepoCheckoutPool.Sanitize("repo-id"));
        Assert.Equal("repo_id", RepoCheckoutPool.Sanitize("repo\\id"));
        Assert.NotEqual(RepoCheckoutPool.CheckoutKey("a/b", "head"), RepoCheckoutPool.CheckoutKey("a_b", "head"));
    }

    [Fact]
    public async Task Acquire_returns_per_head_checkout_and_dispose_is_idempotent()
    {
        var git = new TestGitOps();
        var pool = Pool(git);

        var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.Equal(pool.CheckoutPath("repo", "head"), checkout.Path);
        Assert.True(Directory.Exists(checkout.Path));
        checkout.Dispose();
        checkout.Dispose();

        using var second = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.Equal(2, git.CheckoutCount);
    }

    [Fact]
    public async Task CheckoutDirectoryCount_counts_materialized_heads()
    {
        var pool = Pool(new TestGitOps());
        Assert.Equal(0, pool.CheckoutDirectoryCount()); // checkout root missing

        using var a = await pool.AcquireAsync("repo", "url", "base", "head1", CancellationToken.None);
        using var b = await pool.AcquireAsync("repo", "url", "base", "head2", CancellationToken.None);
        Assert.Equal(2, pool.CheckoutDirectoryCount());
    }

    [Fact]
    public async Task Acquire_ensures_commits_before_checkout()
    {
        var git = new TestGitOps();
        var pool = Pool(git);

        using var checkout = await pool.AcquireAsync("repo", "url", "base-sha", "head-sha", CancellationToken.None);

        Assert.Equal([("base-sha", "head-sha")], git.EnsuredCommits);
        Assert.Equal(["clone", "ensure", "checkout"], git.Calls);
    }

    [Fact]
    public async Task Different_heads_materialize_concurrently()
    {
        var git = new TestGitOps {CloneBarrier = new Barrier(2)};
        var pool = Pool(git);

        // Use dedicated workers and coordinate at the clone boundary instead of relying on timing.
        var first = Task.Factory.StartNew(
            () => pool.AcquireAsync("repo", "url", "base", "head-a", CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        var second = Task.Factory.StartNew(
            () => pool.AcquireAsync("repo", "url", "base", "head-b", CancellationToken.None),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        using var a = await first;
        using var b = await second;

        Assert.Equal(2, git.CheckoutCount);
        Assert.True(git.MaxConcurrentClones > 1, "independent heads should clone concurrently, not serialize");
    }

    [Fact]
    public async Task Evict_removes_oldest_over_repo_cap()
    {
        var pool = Pool(new TestGitOps());
        var repoDir = Path.GetDirectoryName(pool.CheckoutPath("repo", "old"))!;
        var old = Path.Combine(repoDir, "old");
        var middle = Path.Combine(repoDir, "middle");
        var newest = Path.Combine(repoDir, "newest");
        foreach (var path in new[] {old, middle, newest})
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "file"), "data");
        }

        var now = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(old, now.AddMinutes(-3));
        Directory.SetLastWriteTimeUtc(middle, now.AddMinutes(-2));
        Directory.SetLastWriteTimeUtc(newest, now.AddMinutes(-1));

        var report = pool.Evict(new CheckoutEvictionOptions {MaxCheckoutsPerRepo = 2}, TimeProvider.System);

        Assert.Equal(3, report.Scanned);
        Assert.Equal(1, report.Deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(middle));
        Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public async Task Evict_skips_a_checkout_held_by_a_lease()
    {
        var git = new TestGitOps();
        var pool = Pool(git);
        using var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Directory.SetLastWriteTimeUtc(checkout.Path, DateTime.UtcNow.AddDays(-10));

        var report = pool.Evict(new CheckoutEvictionOptions {MaxAge = TimeSpan.FromDays(1)}, TimeProvider.System);

        Assert.Equal(1, report.SkippedInUse);
        Assert.True(Directory.Exists(checkout.Path));
    }

    [Fact]
    public async Task Reuses_checkout_when_HEAD_already_matches()
    {
        var git = new TestGitOps {HeadSha = "head"};
        var pool = Pool(git);
        using (await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None))
        {
        }

        using var second = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);

        Assert.Equal(1, git.CloneCount);
        Assert.Equal(1, git.CheckoutCount);
    }

    [Fact]
    public async Task Acquire_failure_releases_lock()
    {
        var git = new TestGitOps {ThrowOnClone = true};
        var pool = Pool(git);
        await Assert.ThrowsAsync<IOException>(() => pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None));

        // The failed acquire must not leave the keyed semaphore behind: the same pool and key
        // must be acquirable once the clone stops failing.
        git.ThrowOnClone = false;
        using var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.True(Directory.Exists(checkout.Path));
    }

    [Fact]
    public async Task Acquire_propagates_cancellation_during_clone()
    {
        var git = new TestGitOps {Delay = TimeSpan.FromSeconds(5)};
        var pool = Pool(git);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pool.AcquireAsync("repo", "url", "base", "head", cts.Token));

        // The cancelled acquire must release the keyed lock: a fresh acquire succeeds.
        git.Delay = TimeSpan.Zero;
        using var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.True(Directory.Exists(checkout.Path));
    }

    [Fact]
    public void Evict_cleans_an_empty_repository_directory()
    {
        var pool = Pool(new TestGitOps());
        var repoDir = Path.GetDirectoryName(pool.CheckoutPath("repo", "head"))!;
        var checkout = pool.CheckoutPath("repo", "head");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "file"), "data");
        pool.Evict(new CheckoutEvictionOptions {MaxCheckoutsPerRepo = 0}, TimeProvider.System);
        Assert.False(Directory.Exists(repoDir));
    }

    [Fact]
    public async Task Keyed_lock_pool_prunes_after_release_and_honors_cancellation()
    {
        var locks = new KeyedLockPool();
        using (await locks.AcquireAsync("one", CancellationToken.None))
        {
        }

        Assert.Equal(0, locks.Count);

        using var held = await locks.AcquireAsync("one", CancellationToken.None);
        Assert.Null(locks.TryAcquire("one"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync("one", canceled.Token));
    }

    [Fact]
    public async Task Keyed_lock_pool_prunes_after_cancelled_waiter_and_release()
    {
        var locks = new KeyedLockPool();

        using var held = await locks.AcquireAsync("key", CancellationToken.None);

        // A second waiter queues for the same key and is cancelled while blocked behind the holder.
        using var cts = new CancellationTokenSource();
        var pending = locks.AcquireAsync("key", cts.Token);
        cts.Cancel();

        // The semaphore does not guarantee whether a blocked waiter observes cancellation or wins
        // the now-free slot on release; either path must still prune the key and leave the
        // semaphore reusable (no idle-entry leak, no premature disposal).
        held!.Dispose();

        KeyedLockPool.Lease? waiter = null;
        try
        {
            waiter = await pending;
        }
        catch (OperationCanceledException)
        {
        }

        waiter?.Dispose();

        Assert.Equal(0, locks.Count);
        using var again = await locks.AcquireAsync("key", CancellationToken.None);
        Assert.NotNull(again);
    }

    [Fact]
    public void Evict_returns_empty_when_disabled_or_checkout_root_missing()
    {
        var pool = Pool(new TestGitOps());
        Assert.Equal(0, pool.Evict(new CheckoutEvictionOptions {Enabled = false}, TimeProvider.System).Scanned);

        Directory.Delete(Path.Combine(_Root, "checkouts"), recursive: true);
        Assert.Equal(0, pool.Evict(new CheckoutEvictionOptions(), TimeProvider.System).Scanned);
    }

    private sealed class TestGitOps : IGitOps
    {
        private readonly object _Gate = new();
        private int _ActiveClones;
        private int _MaxConcurrentClones;
        public Barrier? CloneBarrier { get; init; }
        public TimeSpan Delay { get; set; }
        public bool ThrowOnClone { get; set; }
        public int CheckoutCount { get; private set; }
        public int CloneCount { get; private set; }
        public int MaxConcurrentClones => Volatile.Read(ref _MaxConcurrentClones);
        public string? HeadSha { get; init; }
        public List<(string Base, string Head)> EnsuredCommits { get; } = [];
        public List<string> Calls { get; } = [];

        public async Task<string> CloneOrOpenAsync(string cloneUrl, string workDir, string? pat, CancellationToken ct)
        {
            lock (_Gate)
            {
                Calls.Add("clone");
            }

            if (ThrowOnClone)
            {
                throw new IOException("clone failed");
            }

            var active = Interlocked.Increment(ref _ActiveClones);
            while (true)
            {
                var observed = Volatile.Read(ref _MaxConcurrentClones);
                if (active <= observed || Interlocked.CompareExchange(ref _MaxConcurrentClones, active, observed) == observed)
                {
                    break;
                }
            }

            lock (_Gate)
            {
                CloneCount++;
            }

            try
            {
                if (CloneBarrier is not null && !CloneBarrier.SignalAndWait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("clone operations did not overlap");
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _ActiveClones);
            }

            Directory.CreateDirectory(Path.Combine(workDir, ".git"));
            return workDir;
        }

        public Task CheckoutAsync(string repoPath, string commitSha, CancellationToken ct)
        {
            lock (_Gate)
            {
                Calls.Add("checkout");
                CheckoutCount++;
            }

            return Task.CompletedTask;
        }

        public Task<string?> GetHeadShaAsync(string repoPath, CancellationToken ct) => Task.FromResult(HeadSha);

        public Task EnsureCommitsAsync(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat, CancellationToken ct)
        {
            lock (_Gate)
            {
                Calls.Add("ensure");
                EnsuredCommits.Add((baseSha, headSha));
            }

            return Task.CompletedTask;
        }

        public Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct) => Task.FromResult(string.Empty);
    }
}