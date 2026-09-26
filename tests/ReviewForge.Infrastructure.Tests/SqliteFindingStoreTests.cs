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
    public async Task Ping_succeeds_against_real_store()
    {
        await _Store.PingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Empty_store_returns_null_and_no_keys()
    {
        Assert.Null(await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None));
        Assert.Empty(await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None));
        Assert.Empty(await _Store.GetRecentRunsAsync(Key, 10, CancellationToken.None));
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
    public async Task GetRecentRuns_returns_newest_first_across_outcomes()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h1", ReviewKind.Full, t0, t0.AddMinutes(5), false, []), CancellationToken.None);
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h2", ReviewKind.Full, t0.AddMinutes(10), t0.AddMinutes(15), true, []), CancellationToken.None);

        var runs = await _Store.GetRecentRunsAsync(Key, 10, CancellationToken.None);

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Success);
        Assert.Equal("h2", runs[0].HeadSha);
        Assert.False(runs[1].Success);
        Assert.Equal("h1", runs[1].HeadSha);
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
    public async Task GetStaleShells_returns_only_old_uncompleted_runs_across_prs()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var cutoff = t0.AddMinutes(30);
        // Stale shell (old, uncompleted) — must be returned.
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h1", ReviewKind.Full, t0, null, false, []), CancellationToken.None);
        // Recent shell (started after the cutoff — still inside the stages 75→100 window) — must NOT be returned.
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h2", ReviewKind.Full, cutoff.AddMinutes(1), null, false, []), CancellationToken.None);
        // Completed run, even an old one — must NOT be returned.
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h3", ReviewKind.Full, t0, t0.AddMinutes(4), false, []), CancellationToken.None);
        // Stale shell on another PR — must be returned (reaper scans all PRs).
        var otherPr = Key with {PrId = 99};
        var otherShell = new ReviewRun(Guid.NewGuid(), otherPr, "h4", ReviewKind.Full, t0, null, false, []);
        await _Store.SaveRunAsync(otherShell, CancellationToken.None);

        var shells = await _Store.GetStaleShellsAsync(cutoff, CancellationToken.None);

        Assert.Equal(2, shells.Count);
        Assert.Contains(shells, r => r.HeadSha == "h1");
        Assert.Contains(shells, r => r.Id == otherShell.Id && r.Pr == otherPr);
        Assert.DoesNotContain(shells, r => r.HeadSha is "h2" or "h3");
        Assert.All(shells, r => Assert.Null(r.CompletedAt));
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

    [Fact]
    public async Task Save_and_read_round_trips_last_observed_comment_at()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var watermark = t0.AddMinutes(3);
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full, t0.AddMinutes(-5), t0, true,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, null)],
            LastObservedCommentAt: watermark);

        await _Store.SaveRunAsync(run, CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.Equal(watermark, last!.LastObservedCommentAt);
        Assert.Equal(["k1"], last.FindingKeys);
    }

    [Fact]
    public async Task Constructor_upgrades_existing_database_without_watermark_column()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "reviewforge-legacy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // A database created by an older binary: Runs without LastObservedCommentAt.
            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Runs (
                        Id TEXT PRIMARY KEY, Org TEXT NOT NULL, Project TEXT NOT NULL,
                        RepositoryId TEXT NOT NULL, PrId INTEGER NOT NULL, HeadSha TEXT NOT NULL,
                        Kind TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT NULL, Success INTEGER NOT NULL);
                    CREATE TABLE Findings (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, DedupeKey TEXT NOT NULL,
                        RuleId TEXT NOT NULL, Severity TEXT NOT NULL, Title TEXT NOT NULL,
                        FilePath TEXT NULL, Line INTEGER NULL, ThreadId INTEGER NULL);
                    """;
                cmd.ExecuteNonQuery();
            }

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(
                () => new SqliteFindingStore($"Data Source={dbPath};Pooling=False"))));

            using var check = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            check.Open();
            using var pragma = check.CreateCommand();
            pragma.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'LastObservedCommentAt'";
            Assert.Equal(1L, Convert.ToInt64(pragma.ExecuteScalar()));
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
    public async Task Legacy_row_without_watermark_returns_null_watermark()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(new ReviewRun(
            Guid.NewGuid(), Key, "head", ReviewKind.Full, t0.AddMinutes(-5), t0, true,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, null)],
            LastObservedCommentAt: null), CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);

        Assert.NotNull(last);
        Assert.Null(last.LastObservedCommentAt);
    }

    [Fact]
    public async Task PruneAsync_deletes_old_runs_keeping_min_runs_and_latest_completed()
    {
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        // 8 completed runs, one per day; each carries a finding row with a thread-id backfill.
        for (var i = 0; i < 8; i++)
        {
            var started = t0.AddDays(i);
            await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, $"h{i}", ReviewKind.Full,
                    started, started.AddMinutes(5), true,
                    [new StoredFinding($"k{i}", "rule", "high", "title", "f.cs", 1, 42)]),
                CancellationToken.None);
        }

        // Cutoff at day 5: days 0-4 prunable; last 3 runs (days 5-7) stay, latest completed
        // (day 7) already inside that window.
        var pruned = await _Store.PruneAsync(t0.AddDays(5), minRunsPerPr: 3, CancellationToken.None);

        Assert.Equal(5, pruned);
        var recent = await _Store.GetRecentRunsAsync(Key, 20, CancellationToken.None);
        Assert.Equal(["h7", "h6", "h5"], recent.Select(r => r.HeadSha).ToArray());
        // Finding rows of pruned runs are gone; kept rows intact (thread-id backfill preserved).
        Assert.Equal(["k5", "k6", "k7"], (await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None)).Order().ToArray());
        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal("h7", last!.HeadSha);
        Assert.Equal(42, last.Findings!.Single(f => f.DedupeKey == "k7").ThreadId);
    }

    [Fact]
    public async Task PruneAsync_never_deletes_the_latest_completed_run()
    {
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        // One old completed run, then five newer shells that never completed.
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "old-done", ReviewKind.Full,
                t0, t0.AddMinutes(5), true, [new StoredFinding("k-old", "rule", "high", "t", "f.cs", 1, null)]),
            CancellationToken.None);
        for (var i = 1; i <= 5; i++)
        {
            await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, $"shell{i}", ReviewKind.Full,
                    t0.AddDays(i), CompletedAt: null, Success: false, []),
                CancellationToken.None);
        }

        // Cutoff past everything; min-keep 2 covers only the newest shells — the old
        // completed run must survive via the latest-completed exemption.
        var pruned = await _Store.PruneAsync(t0.AddDays(10), minRunsPerPr: 2, CancellationToken.None);

        Assert.Equal(3, pruned);
        var recent = await _Store.GetRecentRunsAsync(Key, 20, CancellationToken.None);
        Assert.Equal(3, recent.Count);
        Assert.Contains(recent, r => r.HeadSha == "old-done");
        Assert.Equal(2, recent.Count(r => r.CompletedAt is null));
        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal("old-done", last!.HeadSha);
        Assert.Equal(["k-old"], last.FindingKeys);
    }

    [Fact]
    public async Task GetLastCompletedRunAsync_returns_true_latest_across_50_completed_runs()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 50; i++)
        {
            var started = t0.AddMinutes(i * 10);
            await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, $"h{i}", ReviewKind.Full,
                    started, started.AddMinutes(5), i % 3 != 0, // failures sprinkled in
                    [new StoredFinding($"k{i}", "rule", "high", "title", "f.cs", 1, null)]),
                CancellationToken.None);
        }

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal("h49", last!.HeadSha); // newest started; per-PR runs never overlap
        Assert.Equal(t0.AddMinutes(49 * 10).AddMinutes(5), last.CompletedAt);
        Assert.Equal(["k49"], last.FindingKeys);
    }

    [Fact]
    public async Task Reads_stay_bounded_and_correct_at_300_runs_with_40_findings_each()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);
        var findings = Enumerable.Range(0, 40)
            .Select(i => new StoredFinding($"k{i}", "rule", "high", "title", "f.cs", i, null))
            .ToArray();
        for (var i = 0; i < 300; i++)
        {
            var started = t0.AddMinutes(i);
            await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, $"h{i}", ReviewKind.Full,
                    started, started.AddMinutes(1), i % 5 != 0, findings),
                CancellationToken.None);
        }

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal("h299", last!.HeadSha);
        Assert.Equal(40, last.Findings!.Count);

        var recent = await _Store.GetRecentRunsAsync(Key, 10, CancellationToken.None);
        Assert.Equal(10, recent.Count);
        Assert.Equal("h299", recent[0].HeadSha);
        Assert.Equal("h290", recent[9].HeadSha);
        Assert.All(recent, r => Assert.Equal(40, r.Findings.Count));
    }

    [Fact]
    public async Task AppliedFixJson_roundtrips_with_the_finding()
    {
        var fixJson = """{"DedupeKey":"k1","Proposal":{"FilePath":"script.sh","StartLine":3,"EndLine":3,"Replacement":"echo \"$name\"","Rationale":"quote it","Origin":"Deterministic","SourceThreadId":null},"VerifierName":"none"}""";
        var completed = DateTimeOffset.UtcNow;
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h", ReviewKind.Full,
                completed.AddMinutes(-5), completed, Success: true,
                [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, 42, fixJson)]),
            CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);
        var finding = Assert.Single(last!.Findings!);
        Assert.Equal(fixJson, finding.AppliedFixJson);
    }

    [Fact]
    public async Task Legacy_table_without_AppliedFixJson_column_is_migrated_and_writable()
    {
        // Simulate a database created before the auto-fix feature: both tables, no
        // AppliedFixJson column on Findings. A separate database file — the test-class
        // store has already EnsureCreated its own schema.
        var dbPath = Path.Combine(Path.GetTempPath(), "reviewforge-legacy-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        try
        {
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE "Runs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Runs" PRIMARY KEY,
                    "Org" TEXT NOT NULL,
                    "Project" TEXT NOT NULL,
                    "RepositoryId" TEXT NOT NULL,
                    "PrId" INTEGER NOT NULL,
                    "HeadSha" TEXT NOT NULL,
                    "Kind" TEXT NOT NULL,
                    "StartedAt" TEXT NOT NULL,
                    "CompletedAt" TEXT NULL,
                    "LastObservedCommentAt" TEXT NULL,
                    "Success" INTEGER NOT NULL
                );
                CREATE TABLE "Findings" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Findings" PRIMARY KEY AUTOINCREMENT,
                    "RunId" TEXT NOT NULL,
                    "DedupeKey" TEXT NOT NULL,
                    "RuleId" TEXT NOT NULL,
                    "Severity" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "FilePath" TEXT NULL,
                    "Line" INTEGER NULL,
                    "ThreadId" INTEGER NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Opening the store must add the column; saving and reading back must work.
        var legacyStore = new SqliteFindingStore(connectionString);
        var completed = DateTimeOffset.UtcNow;
        await legacyStore.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "h", ReviewKind.Full,
                completed.AddMinutes(-5), completed, Success: true,
                [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, 42, "{}")]),
            CancellationToken.None);

        var last = await legacyStore.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.Equal("{}", Assert.Single(last!.Findings!).AppliedFixJson);

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Findings') WHERE name = 'AppliedFixJson'";
            Assert.Equal(1L, await cmd.ExecuteScalarAsync());
        }
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
}
