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

    private static void CreateCheckout(string path, int size, DateTime lastWrite)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "blob"), new byte[size]);
        Directory.SetLastWriteTimeUtc(path, lastWrite);
    }

    [Fact]
    public void Evict_enforces_global_budget_cross_repo_lru()
    {
        var pool = Pool(new TestGitOps());
        const int oneMb = 1_000_000;
        var now = DateTime.UtcNow;

        var a1 = pool.CheckoutPath("a", "old1");
        var a2 = pool.CheckoutPath("a", "old2");
        var b1 = pool.CheckoutPath("b", "new1");
        var b2 = pool.CheckoutPath("b", "new2");
        CreateCheckout(a1, oneMb, now.AddMinutes(-3));
        CreateCheckout(a2, oneMb, now.AddMinutes(-2));
        CreateCheckout(b1, oneMb, now.AddMinutes(-1));
        CreateCheckout(b2, oneMb, now);

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = 3L * oneMb, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(oneMb, report.BytesFreed);
        Assert.Equal(3L * oneMb, report.BytesRemaining);
        Assert.False(Directory.Exists(a1)); // oldest cross-repo evicted
        Assert.True(Directory.Exists(a2));
        Assert.True(Directory.Exists(b1));
        Assert.True(Directory.Exists(b2));
    }

    [Fact]
    public void Evict_budget_zero_disables_global_pass()
    {
        var pool = Pool(new TestGitOps());
        const int oneMb = 1_000_000;
        var a1 = pool.CheckoutPath("a", "old1");
        var a2 = pool.CheckoutPath("a", "old2");
        CreateCheckout(a1, oneMb, DateTime.UtcNow.AddMinutes(-2));
        CreateCheckout(a2, oneMb, DateTime.UtcNow);

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = 0, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(0, report.Deleted);
        Assert.True(Directory.Exists(a1));
        Assert.True(Directory.Exists(a2));
    }

    [Fact]
    public async Task Evict_budget_skips_in_use_checkouts()
    {
        var pool = Pool(new TestGitOps());
        const int oneMb = 1_000_000;

        var heldPath = pool.CheckoutPath("repo", "held");
        CreateCheckout(heldPath, oneMb, DateTime.UtcNow);
        using var held = await pool.AcquireAsync("repo", "url", "base", "held", CancellationToken.None);
        Directory.SetLastWriteTimeUtc(heldPath, DateTime.UtcNow.AddMinutes(-5)); // held is oldest after acquire

        var other = pool.CheckoutPath("repo", "other");
        CreateCheckout(other, oneMb, DateTime.UtcNow.AddMinutes(-1));

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = oneMb + oneMb / 2, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(1, report.SkippedInUse);
        Assert.True(Directory.Exists(heldPath)); // in-use survives the budget pass
        Assert.False(Directory.Exists(other));   // next-oldest evicted instead
    }

    [Fact]
    public async Task Evict_budget_reports_over_budget_when_all_excess_in_use()
    {
        var pool = Pool(new TestGitOps());
        const int oneMb = 1_000_000;

        var p1 = pool.CheckoutPath("repo", "h1");
        var p2 = pool.CheckoutPath("repo", "h2");
        CreateCheckout(p1, oneMb, DateTime.UtcNow.AddMinutes(-5));
        CreateCheckout(p2, oneMb, DateTime.UtcNow.AddMinutes(-1));
        using var lease1 = await pool.AcquireAsync("repo", "url", "base", "h1", CancellationToken.None);
        using var lease2 = await pool.AcquireAsync("repo", "url", "base", "h2", CancellationToken.None);

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = oneMb, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(2, report.SkippedInUse);
        Assert.True(report.BytesRemaining > oneMb);
    }

    [Fact]
    public async Task Acquire_populates_size_cache_for_budget_eviction()
    {
        var pool = Pool(new TestGitOps());
        const int oneMb = 1_000_000;

        var path = pool.CheckoutPath("repo", "head");
        CreateCheckout(path, oneMb, DateTime.UtcNow);
        using (var held = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None))
        {
            Directory.SetLastWriteTimeUtc(held.Path, DateTime.UtcNow.AddMinutes(-5));
        }

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = oneMb / 2, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(oneMb, report.BytesFreed);
    }

    [Fact]
    public async Task Acquire_tolerates_size_measurement_failure()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), new ThrowingFs { ThrowOnSize = true }, _Root);

        using var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);

        Assert.True(Directory.Exists(checkout.Path));
    }

    [Fact]
    public void Evict_tolerates_size_failure_for_survivors()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), new ThrowingFs { ThrowOnSize = true }, _Root);
        var path = pool.CheckoutPath("repo", "head");
        CreateCheckout(path, 1000, DateTime.UtcNow);

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = 100, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(0, report.Deleted); // size unknown (0), budget not exceeded
    }

    [Fact]
    public void Evict_reports_delete_failures_from_count_pass()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), new ThrowingFs { ThrowOnDelete = true }, _Root);
        var path = pool.CheckoutPath("repo", "head");
        CreateCheckout(path, 1000, DateTime.UtcNow.AddDays(-10));

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxAge = TimeSpan.FromDays(1), MaxTotalBytes = 0 },
            TimeProvider.System);

        Assert.Equal(1, report.Failed);
        Assert.Single(report.FailureDetails);
    }

    [Fact]
    public void Evict_reports_delete_failures_from_budget_pass()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), new ThrowingFs { ThrowOnDelete = true }, _Root);
        var path = pool.CheckoutPath("repo", "head");
        CreateCheckout(path, 1000, DateTime.UtcNow);

        var report = pool.Evict(
            new CheckoutEvictionOptions { MaxTotalBytes = 100, MaxCheckoutsPerRepo = 10, MaxAge = TimeSpan.FromDays(30) },
            TimeProvider.System);

        Assert.Equal(1, report.Failed);
        Assert.Single(report.FailureDetails);
        Assert.True(report.BytesRemaining > 100); // delete failed, still over budget
    }

    private sealed class ThrowingFs : IWorkspaceFs
    {
        private readonly FakeWorkspaceFs _Inner = new();
        public bool ThrowOnDelete { get; init; }
        public bool ThrowOnSize { get; init; }

        public void CreateDirectory(string path) => _Inner.CreateDirectory(path);
        public bool DirectoryExists(string path) => _Inner.DirectoryExists(path);
        public IReadOnlyList<string> EnumerateDirectories(string path) => _Inner.EnumerateDirectories(path);
        public string[] EnumerateFileSystemEntries(string path) => _Inner.EnumerateFileSystemEntries(path);

        public string[] EnumerateFilesRecursive(string path)
            => ThrowOnSize ? throw new IOException("size failed") : _Inner.EnumerateFilesRecursive(path);

        public long GetFileLength(string path) => _Inner.GetFileLength(path);
        public DateTime GetLastWriteTimeUtc(string path) => _Inner.GetLastWriteTimeUtc(path);
        public void SetLastWriteTimeUtc(string path, DateTime timestamp) => _Inner.SetLastWriteTimeUtc(path, timestamp);

        public void DeleteDirectory(string path, bool recursive)
        {
            if (ThrowOnDelete)
            {
                throw new IOException("delete failed");
            }

            _Inner.DeleteDirectory(path, recursive);
        }
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

        public Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null) => Task.FromResult(string.Empty);
    }
}