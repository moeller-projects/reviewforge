using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class CheckoutEvictionWorkerTests
{
    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>Real temp-dir filesystem whose deletion fails for chosen paths.</summary>
    private sealed class FailingDeleteFs(string? failOnPrefix) : IWorkspaceFs
    {
        private readonly FakeWorkspaceFs _Inner = new();

        public void CreateDirectory(string path) => _Inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _Inner.DirectoryExists(path);

        public IReadOnlyList<string> EnumerateDirectories(string path) => _Inner.EnumerateDirectories(path);

        public string[] EnumerateFileSystemEntries(string path) => _Inner.EnumerateFileSystemEntries(path);

        public string[] EnumerateFilesRecursive(string path) => _Inner.EnumerateFilesRecursive(path);

        public long GetFileLength(string path) => _Inner.GetFileLength(path);

        public DateTime GetLastWriteTimeUtc(string path) => _Inner.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime timestamp) => _Inner.SetLastWriteTimeUtc(path, timestamp);

        public void DeleteDirectory(string path, bool recursive)
        {
            if (failOnPrefix is not null && path.StartsWith(failOnPrefix, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("locked by a native handle");
            }

            _Inner.DeleteDirectory(path, recursive);
        }
    }

    /// <summary>Filesystem whose checkout enumeration throws (simulates a broken mount).</summary>
    private sealed class EnumerateThrowingFs : IWorkspaceFs
    {
        private readonly FakeWorkspaceFs _Inner = new();

        public void CreateDirectory(string path) => _Inner.CreateDirectory(path);

        public bool DirectoryExists(string path) => _Inner.DirectoryExists(path);

        public IReadOnlyList<string> EnumerateDirectories(string path)
            => path.Contains(Path.Combine("checkouts"), StringComparison.Ordinal)
                ? throw new InvalidOperationException("mount unavailable")
                : _Inner.EnumerateDirectories(path);

        public string[] EnumerateFileSystemEntries(string path) => _Inner.EnumerateFileSystemEntries(path);

        public string[] EnumerateFilesRecursive(string path) => _Inner.EnumerateFilesRecursive(path);

        public long GetFileLength(string path) => _Inner.GetFileLength(path);

        public DateTime GetLastWriteTimeUtc(string path) => _Inner.GetLastWriteTimeUtc(path);

        public void SetLastWriteTimeUtc(string path, DateTime timestamp) => _Inner.SetLastWriteTimeUtc(path, timestamp);

        public void DeleteDirectory(string path, bool recursive) => _Inner.DeleteDirectory(path, recursive);
    }

    private static string TempRoot(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static async Task RunUntilAsync(CheckoutEvictionWorker worker, Func<bool> condition, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await worker.StartAsync(cts.Token);
        for (var i = 0; i < timeoutMs / 20 && !condition(); i++)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }
    [Fact]
    public async Task Enabled_worker_sweeps_idle_checkouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-eviction-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "checkouts", "repo", "head"));
        File.WriteAllText(Path.Combine(root, "checkouts", "repo", "head", "file"), "data");
        try
        {
            var pool = new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), root);
            var options = Options.Create(new ReviewForgeServiceOptions
            {
                WorkDir = root,
                Checkout = new CheckoutEvictionOptions
                {
                    Enabled = true,
                    MaxAge = TimeSpan.Zero,
                    SweepInterval = TimeSpan.FromMilliseconds(10),
                },
            });
            var worker = new CheckoutEvictionWorker(
                pool, options, TimeProvider.System, NullLogger<CheckoutEvictionWorker>.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await worker.StartAsync(cts.Token);
            while (Directory.Exists(Path.Combine(root, "checkouts", "repo", "head")))
            {
                await Task.Delay(10, cts.Token);
            }

            await cts.CancelAsync();
            await worker.StopAsync(CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(root, "checkouts", "repo", "head")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Disabled_worker_exits_without_sweeping()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-eviction-disabled-" + Guid.NewGuid().ToString("N"));
        var checkout = Path.Combine(root, "checkouts", "repo", "head");
        Directory.CreateDirectory(checkout);
        try
        {
            var worker = new CheckoutEvictionWorker(
                new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), root),
                Options.Create(new ReviewForgeServiceOptions
                {
                    WorkDir = root,
                    // A short interval makes a "loop started despite Enabled=false" regression
                    // observable within the test, rather than only after an hour-long sweep.
                    Checkout = new CheckoutEvictionOptions
                    {
                        Enabled = false,
                        MaxAge = TimeSpan.Zero,
                        SweepInterval = TimeSpan.FromMilliseconds(10),
                    },
                }),
                TimeProvider.System,
                NullLogger<CheckoutEvictionWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(80); // several would-be ticks
            Assert.True(Directory.Exists(checkout));

            await worker.StopAsync(CancellationToken.None);
            Assert.True(Directory.Exists(checkout));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Over_budget_sweep_warns_but_keeps_in_use_checkout()
    {
        var root = TempRoot("reviewforge-eviction-budget-");
        var pool = new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), root);
        var options = Options.Create(new ReviewForgeServiceOptions
        {
            WorkDir = root,
            Checkout = new CheckoutEvictionOptions
            {
                Enabled = true,
                MaxAge = TimeSpan.FromHours(1), // the just-created checkout counts as fresh
                SweepInterval = TimeSpan.FromMilliseconds(10),
                MaxTotalBytes = 1024,
            },
        });

        // The lease is held for the whole sweep, so the checkout is in use: the budget
        // pass must NOT evict it, leaving BytesRemaining over budget and tripping the warning.
        // Payload is written into the (hashed) checkout path BEFORE acquire so the pool's
        // size cache measures the real bytes.
        var checkoutPath = pool.CheckoutPath("repo", "head");
        Directory.CreateDirectory(checkoutPath);
        File.WriteAllText(Path.Combine(checkoutPath, "payload"), new string('x', 4096));
        var checkout = await pool.AcquireAsync("repo", "https://example/repo", "base", "head", CancellationToken.None);
        try
        {
            var logger = new CollectingLogger<CheckoutEvictionWorker>();
            var worker = new CheckoutEvictionWorker(pool, options, TimeProvider.System, logger);

            await RunUntilAsync(worker, () => logger.Messages.Any(m => m.Contains("disk budget exceeded")));

            Assert.Contains(logger.Messages, m => m.Contains("disk budget exceeded"));
            Assert.True(Directory.Exists(checkout.Path), "in-use checkout must survive the budget pass");
        }
        finally
        {
            checkout.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Sweep_reports_failed_deletions()
    {
        var root = TempRoot("reviewforge-eviction-faildel-");
        var checkout = Path.Combine(root, "checkouts", "repo", "head");
        Directory.CreateDirectory(checkout);
        File.WriteAllText(Path.Combine(checkout, "payload"), "data");
        var fs = new FailingDeleteFs(checkout);
        var pool = new RepoCheckoutPool(new FakeGitOps(), fs, root);
        var options = Options.Create(new ReviewForgeServiceOptions
        {
            WorkDir = root,
            Checkout = new CheckoutEvictionOptions
            {
                Enabled = true,
                MaxAge = TimeSpan.Zero,
                MaxCheckoutsPerRepo = 0, // below the per-repo keep count -> deletion path
                SweepInterval = TimeSpan.FromMilliseconds(10),
            },
        });
        try
        {
            var logger = new CollectingLogger<CheckoutEvictionWorker>();
            var worker = new CheckoutEvictionWorker(pool, options, TimeProvider.System, logger);

            await RunUntilAsync(worker, () => logger.Messages.Any(m => m.Contains("failed to delete")));

            Assert.Contains(logger.Messages, m => m.Contains("failed to delete 1 checkouts"));
            Assert.True(Directory.Exists(checkout), "failed deletion keeps the checkout on disk");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Sweep_error_is_logged_and_worker_continues()
    {
        var root = TempRoot("reviewforge-eviction-throw-");
        var pool = new RepoCheckoutPool(new FakeGitOps(), new EnumerateThrowingFs(), root);
        var options = Options.Create(new ReviewForgeServiceOptions
        {
            WorkDir = root,
            Checkout = new CheckoutEvictionOptions
            {
                Enabled = true,
                MaxAge = TimeSpan.Zero,
                SweepInterval = TimeSpan.FromMilliseconds(10),
            },
        });
        try
        {
            var logger = new CollectingLogger<CheckoutEvictionWorker>();
            var worker = new CheckoutEvictionWorker(pool, options, TimeProvider.System, logger);

            await RunUntilAsync(worker, () => logger.Messages.Any(m => m.Contains("checkout eviction sweep failed")));

            Assert.Contains(logger.Messages, m => m.Contains("checkout eviction sweep failed"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}