using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
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
        var pool = new RepoCheckoutPool(git, _Root);

        var checkout = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.Equal(pool.CheckoutPath("repo", "head"), checkout.Path);
        Assert.True(Directory.Exists(checkout.Path));
        checkout.Dispose();
        checkout.Dispose();

        using var second = await pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None);
        Assert.Equal(2, git.CheckoutCount);
    }

    [Fact]
    public async Task Acquire_ensures_commits_before_checkout()
    {
        var git = new TestGitOps();
        var pool = new RepoCheckoutPool(git, _Root);

        using var checkout = await pool.AcquireAsync("repo", "url", "base-sha", "head-sha", CancellationToken.None);

        Assert.Equal([("base-sha", "head-sha")], git.EnsuredCommits);
        Assert.Equal(["clone", "ensure", "checkout"], git.Calls);
    }

    [Fact]
    public async Task Different_heads_can_materialize_concurrently()
    {
        var git = new TestGitOps {Delay = TimeSpan.FromMilliseconds(50)};
        var pool = new RepoCheckoutPool(git, _Root);

        using var first = await pool.AcquireAsync("repo", "url", "base", "head-a", CancellationToken.None);
        using var second = await pool.AcquireAsync("repo", "url", "base", "head-b", CancellationToken.None);

        Assert.Equal(2, git.CheckoutCount);
    }

    [Fact]
    public async Task Evict_removes_oldest_over_repo_cap()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), _Root);
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
        var pool = new RepoCheckoutPool(git, _Root);
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
        var pool = new RepoCheckoutPool(git, _Root);
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
        var pool = new RepoCheckoutPool(new TestGitOps {ThrowOnClone = true}, _Root);
        await Assert.ThrowsAsync<IOException>(() => pool.AcquireAsync("repo", "url", "base", "head", CancellationToken.None));
    }

    [Fact]
    public void Evict_cleans_an_empty_repository_directory()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), _Root);
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
    public void Evict_returns_empty_when_disabled_or_checkout_root_missing()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), _Root);
        Assert.Equal(0, pool.Evict(new CheckoutEvictionOptions {Enabled = false}, TimeProvider.System).Scanned);

        Directory.Delete(Path.Combine(_Root, "checkouts"), recursive: true);
        Assert.Equal(0, pool.Evict(new CheckoutEvictionOptions(), TimeProvider.System).Scanned);
    }

    private sealed class TestGitOps : IGitOps
    {
        public TimeSpan Delay { get; init; }
        public bool ThrowOnClone { get; init; }
        public int CheckoutCount { get; private set; }
        public int CloneCount { get; private set; }
        public string? HeadSha { get; init; }
        public List<(string Base, string Head)> EnsuredCommits { get; } = [];
        public List<string> Calls { get; } = [];

        public string CloneOrOpen(string cloneUrl, string workDir, string? pat)
        {
            Calls.Add("clone");
            if (ThrowOnClone)
            {
                throw new IOException("clone failed");
            }

            CloneCount++;
            Directory.CreateDirectory(Path.Combine(workDir, ".git"));
            return workDir;
        }

        public void Checkout(string repoPath, string commitSha)
        {
            Calls.Add("checkout");
            CheckoutCount++;
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }
        }

        public string? GetHeadSha(string repoPath) => HeadSha;

        public void EnsureCommits(string repoPath, string cloneUrl, string baseSha, string headSha, string? pat)
        {
            Calls.Add("ensure");
            EnsuredCommits.Add((baseSha, headSha));
        }

        public string GetDiff(string repoPath, string baseSha, string headSha) => string.Empty;
    }
}