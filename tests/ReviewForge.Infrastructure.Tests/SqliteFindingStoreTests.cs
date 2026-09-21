using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Infrastructure.Persistence;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class SqliteFindingStoreTests : IDisposable
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);
    private readonly string _ConnectionString;
    private readonly string _DbPath;
    private readonly SqliteFindingStore _Store;

    public SqliteFindingStoreTests()
    {
        _DbPath = Path.Combine(Path.GetTempPath(), "reviewforge-store-" + Guid.NewGuid().ToString("N") + ".db");
        _ConnectionString = $"Data Source={_DbPath};Pooling=False";
        _Store = new SqliteFindingStore(_ConnectionString);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] {"", "-wal", "-shm"})
        {
            if (File.Exists(_DbPath + suffix))
            {
                File.Delete(_DbPath + suffix);
            }
        }
    }

    private static ReviewRun Run(string head, DateTimeOffset completed, bool success = true, params string[] keys)
        => new(Guid.NewGuid(), Key, head, ReviewKind.Full, completed.AddMinutes(-5), completed, success,
            [.. keys.Select(k => new StoredFinding(k, "rule", "high", "title", "f.cs", 1, null))]);

    [Fact]
    public async Task Empty_store_returns_null_and_no_keys()
    {
        Assert.Null(await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None));
        Assert.Empty(await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Store_enables_wal_journal_mode()
    {
        await _Store.SaveRunAsync(Run("h", DateTimeOffset.UtcNow), CancellationToken.None);

        await using var connection = new SqliteConnection(_ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqliteCommand("PRAGMA journal_mode", connection);
        Assert.Equal("wal", Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Interceptor_sets_busy_timeout_on_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        SqliteBusyTimeoutInterceptor.ApplyBusyTimeout(connection, 5000);

        await using var command = new SqliteCommand("PRAGMA busy_timeout", connection);
        Assert.Equal(5000L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Parallel_saves_from_two_stores_do_not_fail()
    {
        var second = new SqliteFindingStore(_ConnectionString);
        var t0 = DateTimeOffset.UtcNow;

        var saves = Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? _Store : second).SaveRunAsync(Run($"head-{i}", t0.AddSeconds(i)), CancellationToken.None));
        await Task.WhenAll(saves);

        Assert.NotNull(await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Save_and_read_back_latest_completed_run()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(Run("old-head", t0, keys: ["k1"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("new-head", t0.AddHours(1), keys: ["k2"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("failed-head", t0.AddHours(2), success: false), CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal("new-head", last.HeadSha); // failed run ignored
        Assert.Equal(["k2"], last.FindingKeys);
    }

    [Fact]
    public async Task GetLastCompletedRun_returns_finding_rows()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full, t0.AddMinutes(-5), t0, true,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, 42)]);

        await _Store.SaveRunAsync(run, CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(["k1"], last.FindingKeys);
        var finding = Assert.Single(last.Findings!);
        Assert.Equal("k1", finding.DedupeKey);
        Assert.Equal("rule", finding.RuleId);
        Assert.Equal("high", finding.Severity);
        Assert.Equal("title", finding.Title);
        Assert.Equal("f.cs", finding.FilePath);
        Assert.Equal(1, finding.Line);
        Assert.Equal(42, finding.ThreadId);
    }

    [Fact]
    public async Task BeginRun_shell_is_invisible_to_GetLastCompletedRun()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var shellId = Guid.NewGuid();

        await _Store.SaveRunAsync(new ReviewRun(shellId, Key, "head", ReviewKind.Full, t0, null, false,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, null)]), CancellationToken.None);

        Assert.Null(await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None));

        // Backfill the thread id while the run is still in-flight.
        await _Store.SetThreadIdAsync(shellId, "k1", 42, CancellationToken.None);

        await _Store.SaveRunAsync(new ReviewRun(shellId, Key, "head", ReviewKind.Full, t0, t0.AddMinutes(5), true,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, null)]), CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal("head", last.HeadSha);
        Assert.Equal(["k1"], last.FindingKeys);
        Assert.Equal(42, Assert.Single(last.Findings!).ThreadId); // backfill preserved through finalize
    }

    [Fact]
    public async Task SaveRun_finalize_merges_without_losing_thread_ids()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var runId = Guid.NewGuid();

        await _Store.SaveRunAsync(new ReviewRun(runId, Key, "head", ReviewKind.Full, t0, null, false,
            [
                new StoredFinding("k1", "r", "high", "t1", "f.cs", 1, null),
                new StoredFinding("k2", "r", "high", "t2", "f.cs", 2, null),
            ]), CancellationToken.None);

        await _Store.SetThreadIdAsync(runId, "k1", 1000, CancellationToken.None);

        await _Store.SaveRunAsync(new ReviewRun(runId, Key, "head", ReviewKind.Full, t0, t0.AddMinutes(5), true,
            [
                new StoredFinding("k1", "r", "high", "t1", "f.cs", 1, null),
                new StoredFinding("k2", "r", "high", "t2", "f.cs", 2, null),
            ]), CancellationToken.None);

        await using (var db = new FindingStoreDbContext(
                         new DbContextOptionsBuilder<FindingStoreDbContext>()
                             .UseSqlite(_ConnectionString).Options))
        {
            var runs = await db.Runs.Include(r => r.Findings).ToListAsync();
            var run = Assert.Single(runs);
            Assert.True(run.Success);
            Assert.NotNull(run.CompletedAt);
            Assert.Equal(2, run.Findings.Count);
            Assert.Equal(1000, run.Findings.Single(f => f.DedupeKey == "k1").ThreadId);
            Assert.Null(run.Findings.Single(f => f.DedupeKey == "k2").ThreadId);
        }
    }

    [Fact]
    public async Task SaveRun_finalize_merges_new_findings()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var runId = Guid.NewGuid();

        await _Store.SaveRunAsync(new ReviewRun(runId, Key, "head", ReviewKind.Full, t0, null, false,
            [new StoredFinding("k1", "r", "high", "t1", "f.cs", 1, null)]), CancellationToken.None);

        // Finalize introduces a finding the shell did not have; it must be merged.
        await _Store.SaveRunAsync(new ReviewRun(runId, Key, "head", ReviewKind.Full, t0, t0.AddMinutes(5), true,
            [
                new StoredFinding("k1", "r", "high", "t1", "f.cs", 1, null),
                new StoredFinding("k2", "r", "high", "t2", "f.cs", 2, null),
            ]), CancellationToken.None);

        await using (var db = new FindingStoreDbContext(
                         new DbContextOptionsBuilder<FindingStoreDbContext>()
                             .UseSqlite(_ConnectionString).Options))
        {
            var run = Assert.Single(await db.Runs.Include(r => r.Findings).ToListAsync());
            Assert.Equal(2, run.Findings.Count);
            Assert.Contains(run.Findings, f => f.DedupeKey == "k1");
            Assert.Contains(run.Findings, f => f.DedupeKey == "k2");
        }
    }

    [Fact]
    public async Task Known_keys_are_distinct_across_runs_and_scoped_to_pr()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(Run("h1", t0, keys: ["k1", "k2"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("h2", t0.AddHours(1), keys: ["k2", "k3"]), CancellationToken.None);

        var otherPr = Key with {PrId = 99};
        await _Store.SaveRunAsync(
            new ReviewRun(Guid.NewGuid(), otherPr, "hx", ReviewKind.Full, t0, t0, true,
                [new StoredFinding("other", "r", "s", "t", null, null, null)]),
            CancellationToken.None);

        var keys = await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None);
        Assert.Equal(["k1", "k2", "k3"], keys.Order());

        var otherKeys = await _Store.GetKnownDedupeKeysAsync(otherPr, CancellationToken.None);
        Assert.Equal(["other"], otherKeys);
    }

    [Fact]
    public async Task SetThreadId_backfills_finding()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var run = Run("h", t0, keys: ["k1"]);
        await _Store.SaveRunAsync(run, CancellationToken.None);

        await _Store.SetThreadIdAsync(run.Id, "k1", 1234, CancellationToken.None);

        // Read back through a fresh context to prove persistence.
        await using (var db = new FindingStoreDbContext(
                         new DbContextOptionsBuilder<FindingStoreDbContext>()
                             .UseSqlite(_ConnectionString).Options))
        {
            var entity = Assert.Single(db.Findings);
            Assert.Equal(1234, entity.ThreadId);
            Assert.Equal("f.cs", entity.FilePath);
            Assert.Equal(1, entity.Line);
            Assert.Equal("rule", entity.RuleId);
            Assert.Equal("high", entity.Severity);
            Assert.Equal("title", entity.Title);
            Assert.Equal(run.Id, entity.RunId);
            Assert.True(entity.Id > 0);
        }

        var verify = new SqliteFindingStore(_ConnectionString);
        var last = await verify.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.Equal(["k1"], last!.FindingKeys);
    }
}