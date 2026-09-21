using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class CheckoutEvictionWorkerTests
{
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
}