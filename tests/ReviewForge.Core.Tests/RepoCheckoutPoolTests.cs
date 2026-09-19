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
    }

    [Fact]
    public async Task Acquire_returns_per_head_checkout_and_dispose_is_idempotent()
    {
        var git = new TestGitOps();
        var pool = new RepoCheckoutPool(git, _Root);

        var checkout = await pool.AcquireAsync("repo", "url", "head", CancellationToken.None);
        Assert.Equal(Path.Combine(_Root, "checkouts", "repo", "head"), checkout.Path);
        Assert.True(Directory.Exists(checkout.Path));
        checkout.Dispose();
        checkout.Dispose();

        using var second = await pool.AcquireAsync("repo", "url", "head", CancellationToken.None);
        Assert.Equal(2, git.CheckoutCount);
    }

    [Fact]
    public async Task Different_heads_can_materialize_concurrently()
    {
        var git = new TestGitOps {Delay = TimeSpan.FromMilliseconds(50)};
        var pool = new RepoCheckoutPool(git, _Root);

        using var first = await pool.AcquireAsync("repo", "url", "head-a", CancellationToken.None);
        using var second = await pool.AcquireAsync("repo", "url", "head-b", CancellationToken.None);

        Assert.Equal(2, git.CheckoutCount);
    }

    [Fact]
    public async Task Evict_removes_oldest_over_repo_cap()
    {
        var pool = new RepoCheckoutPool(new TestGitOps(), _Root);
        var repoDir = Path.Combine(_Root, "checkouts", "repo");
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
        using var checkout = await pool.AcquireAsync("repo", "url", "head", CancellationToken.None);
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
        using (await pool.AcquireAsync("repo", "url", "head", CancellationToken.None))
        {
        }

        using var second = await pool.AcquireAsync("repo", "url", "head", CancellationToken.None);

        Assert.Equal(1, git.CloneCount);
        Assert.Equal(1, git.CheckoutCount);
    }

    private sealed class TestGitOps : IGitOps
    {
        public TimeSpan Delay { get; init; }
        public int CheckoutCount { get; private set; }
        public int CloneCount { get; private set; }
        public string? HeadSha { get; init; }

        public string CloneOrOpen(string cloneUrl, string workDir, string? pat)
        {
            CloneCount++;
            Directory.CreateDirectory(Path.Combine(workDir, ".git"));
            return workDir;
        }

        public void Checkout(string repoPath, string commitSha)
        {
            CheckoutCount++;
            if (Delay > TimeSpan.Zero)
            {
                Thread.Sleep(Delay);
            }
        }

        public string? GetHeadSha(string repoPath) => HeadSha;
        public void FetchCommits(string repoPath, string? pat, IReadOnlyList<string> refSpecs) { }
        public string GetDiff(string repoPath, string baseSha, string headSha) => string.Empty;
    }
}
