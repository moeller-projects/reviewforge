using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ShellReaperServiceTests
{
    [Fact]
    public async Task Reaper_finalizes_stale_shells_preserving_finding_rows_and_backoff_applies()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "reviewforge-reaper-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteFindingStore($"Data Source={dbPath};Pooling=False");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        try
        {
            var pr = new PrKey("o", "p", "r", 7);
            var staleShellId = Guid.NewGuid();
            await store.SaveRunAsync(new ReviewRun(
                    staleShellId, pr, "head-1", ReviewKind.Full, clock.GetUtcNow().AddHours(-1),
                    CompletedAt: null, Success: false,
                    [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, 42)]),
                CancellationToken.None);
            // A recent shell (still inside the stages 75→100 window) must survive untouched.
            var liveShellId = Guid.NewGuid();
            await store.SaveRunAsync(new ReviewRun(
                    liveShellId, pr, "head-2", ReviewKind.Full, clock.GetUtcNow().AddSeconds(-30),
                    CompletedAt: null, Success: false, []),
                CancellationToken.None);

            var reaper = new ShellReaperService(
                store,
                Options.Create(new ReviewForgeServiceOptions {StaleShellMinutes = 10}),
                NullLogger<ShellReaperService>.Instance,
                clock);
            await reaper.ReapOnceAsync();

            var recent = await store.GetRecentRunsAsync(pr, 10, CancellationToken.None);
            var finalized = Assert.Single(recent, r => r.Id == staleShellId);
            Assert.False(finalized.Success);
            Assert.Equal(clock.GetUtcNow(), finalized.CompletedAt);
            // Finding rows are preserved by the SaveRunAsync upsert (ThreadId backfill intact).
            var finding = Assert.Single(finalized.Findings);
            Assert.Equal("k1", finding.DedupeKey);
            Assert.Equal(42, finding.ThreadId);
            Assert.Null(Assert.Single(recent, r => r.Id == liveShellId).CompletedAt);

            // The next sweep now sees a real failure: normal backoff applies from
            // finalization time (an intentional, bounded block).
            var blockedUntil = FailureBackoff.BlockedUntil(
                await store.GetRecentRunsAsync(pr, 10, CancellationToken.None),
                "head-1", clock.GetUtcNow(), FailureBackoffPolicy.Default);
            Assert.NotNull(blockedUntil);
        }
        finally
        {
            foreach (var suffix in new[] {"", "-wal", "-shm"})
            {
                if (File.Exists(dbPath + suffix))
                {
                    File.Delete(dbPath + suffix);
                }
            }
        }
    }

    [Fact]
    public async Task Reaper_with_no_stale_shells_is_a_no_op()
    {
        var store = new FakeFindingStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
        var reaper = new ShellReaperService(
            store,
            Options.Create(new ReviewForgeServiceOptions {StaleShellMinutes = 10}),
            NullLogger<ShellReaperService>.Instance,
            clock);
        await reaper.ReapOnceAsync();
        Assert.Empty(store.Runs);
    }
}
