using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
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
    public void Ctor_surfaces_the_open_failure_for_an_unwritable_store_path()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "rf-missing-" + Guid.NewGuid().ToString("N"));

        // Schema ensure opens the connection eagerly: an unopenable path fails fast at
        // construction (visible at startup via StoreHealthCheck) instead of first use.
        Assert.Throws<SqliteException>(
            () => new SqliteFindingStore($"Data Source={Path.Combine(missingDir, "x.db")};Pooling=False"));
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
    public async Task Wal_mode_applies_synchronous_normal_to_store_connections()
    {
        await _Store.SaveRunAsync(Run("h", DateTimeOffset.UtcNow), CancellationToken.None);

        // synchronous is a per-connection pragma — unlike journal_mode it is NOT persisted
        // in the file, so a raw connection shows the SQLite default (FULL=2). The contract
        // is that connections going through the store's interceptor carry NORMAL(1).
        await using var connection = new SqliteConnection(_ConnectionString);
        await connection.OpenAsync();
        new SqliteConnectionPragmasInterceptor(journalMode: StoreJournalMode.Wal).ApplyPragmas(connection);
        await using var command = new SqliteCommand("PRAGMA synchronous", connection);
        Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Wal_mode_allows_readers_during_an_open_writer_transaction()
    {
        await _Store.SaveRunAsync(Run("h", DateTimeOffset.UtcNow), CancellationToken.None);

        await using var writer = new SqliteConnection(_ConnectionString);
        await using var reader = new SqliteConnection(_ConnectionString);
        await writer.OpenAsync();
        await reader.OpenAsync();
        var pragmas = new SqliteConnectionPragmasInterceptor();
        pragmas.ApplyPragmas(writer);
        pragmas.ApplyPragmas(reader);

        // WAL: a reader must not block while another connection holds an open write
        // transaction. In rollback-journal mode this read would wait (or SQLITE_BUSY).
        await using (var transaction = writer.BeginTransaction())
        {
            await using (var write = new SqliteCommand(
                "INSERT INTO Runs (Id, Org, Project, RepositoryId, PrId, HeadSha, Kind, Pipeline, StartedAt, Success) " +
                "VALUES ($id, 'o', 'p', 'r', 1, 'h', 'Full', 'Review', $started, 1)", writer, transaction))
            {
                write.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                write.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
                await write.ExecuteNonQueryAsync();
            }

            await using var read = new SqliteCommand("SELECT COUNT(*) FROM Runs", reader);
            var count = await read.ExecuteScalarAsync();
            Assert.NotNull(count); // completed without waiting for the writer
        }
    }

    [Fact]
    public async Task Delete_mode_keeps_rollback_journal_with_full_synchronous()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "reviewforge-store-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Pooling=False: a pooled store connection would keep the file handle open
            // after SaveRunAsync, and the cleanup File.Delete below fails on Windows
            // (Linux unlinks open files). The class fixture uses the same setting.
            var store = new SqliteFindingStore($"Data Source={dbPath};Pooling=False", StoreJournalMode.Delete);
            await store.SaveRunAsync(Run("h", DateTimeOffset.UtcNow), CancellationToken.None);

            await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using (var journal = new SqliteCommand("PRAGMA journal_mode", connection))
                {
                    Assert.Equal("delete", Assert.IsType<string>(await journal.ExecuteScalarAsync()));
                }

                await using (var synchronous = new SqliteCommand("PRAGMA synchronous", connection))
                {
                    Assert.Equal(2L, Assert.IsType<long>(await synchronous.ExecuteScalarAsync()));
                }

                await connection.CloseAsync();
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(dbPath + suffix))
                {
                    File.Delete(dbPath + suffix);
                }
            }
        }
    }

    [Fact]
    public async Task Interceptor_sets_busy_timeout_on_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        new SqliteConnectionPragmasInterceptor().ApplyPragmas(connection);

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
    public async Task GetRunAsync_round_trips_a_completed_run_by_id()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.FollowUp, t0.AddMinutes(-5), t0, true,
            [new StoredFinding("k1", "rule", "high", "title", "f.cs", 1, 42)]);

        await _Store.SaveRunAsync(run, CancellationToken.None);

        var loaded = await _Store.GetRunAsync(run.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(run.Id, loaded.Id);
        Assert.Equal(Key, loaded.Pr);
        Assert.Equal("head", loaded.HeadSha);
        Assert.Equal(ReviewKind.FollowUp, loaded.Kind);
        Assert.Equal(t0.AddMinutes(-5), loaded.StartedAt);
        Assert.Equal(t0, loaded.CompletedAt);
        Assert.True(loaded.Success);
        var finding = Assert.Single(loaded.Findings);
        Assert.Equal("k1", finding.DedupeKey);
        Assert.Equal(42, finding.ThreadId);
    }

    [Fact]
    public async Task Commanded_fix_thread_ids_include_valid_keys_for_the_requested_pr_only()
    {
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true,
            [
                new StoredFinding("thread-42", "rule", "high", "title", "f.cs", 1, null),
                new StoredFinding("thread-not-a-number", "rule", "high", "title", "f.cs", 1, null),
                new StoredFinding("ordinary", "rule", "high", "title", "f.cs", 1, null)
            ]);
        var otherPrRun = new ReviewRun(Guid.NewGuid(), Key with {PrId = 8}, "other",
            ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true,
            [new StoredFinding("thread-99", "rule", "high", "title", "f.cs", 1, null)]);

        await _Store.SaveRunAsync(run, CancellationToken.None);
        await _Store.SaveRunAsync(otherPrRun, CancellationToken.None);

        var ids = await _Store.GetCommandedFixThreadIdsAsync(Key, CancellationToken.None);

        Assert.Equal([42L], ids);
    }

    [Fact]
    public async Task GetRunAsync_returns_null_for_unknown_run_id()
        => Assert.Null(await _Store.GetRunAsync(Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task GetRunAsync_returns_unfinalized_shell_runs_too()
    {
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full,
            DateTimeOffset.UtcNow, CompletedAt: null, Success: false, []);

        await _Store.SaveRunAsync(run, CancellationToken.None);

        var loaded = await _Store.GetRunAsync(run.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Null(loaded.CompletedAt); // still running: visible, not completed
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
    public async Task Pushed_fixes_confirm_then_round_trip_and_mark_replied()
    {
        var now = DateTimeOffset.UtcNow;
        var run = Run("head", now);
        await _Store.SaveRunAsync(run, CancellationToken.None);
        await _Store.SavePushedFixesAsync(
            Key,
            run.Id,
            [new PushedFix(0, run.Id, "k1", "abcdef123456", "fix(src): change", 42, Pushed: false, AiDrafted: false, ReplyPosted: false, now)],
            CancellationToken.None);

        // Push-intent rows are invisible to reply reconciliation until confirmed.
        Assert.Empty(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));

        await _Store.ConfirmPushedFixesAsync(Key, run.Id, CancellationToken.None);

        var saved = Assert.Single(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));
        Assert.True(saved.Id > 0);
        Assert.Equal(run.Id, saved.RunId);
        Assert.Equal("k1", saved.DedupeKey);
        Assert.Equal("abcdef123456", saved.CommitSha);
        Assert.Equal(42, saved.ThreadId);
        Assert.True(saved.Pushed);
        Assert.False(saved.ReplyPosted);

        await _Store.MarkPushedFixRepliedAsync(saved.Id, CancellationToken.None);

        Assert.Empty(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Abandon_deletes_only_unconfirmed_intent_rows()
    {
        var now = DateTimeOffset.UtcNow;
        var run = Run("head", now);
        await _Store.SaveRunAsync(run, CancellationToken.None);
        await _Store.SavePushedFixesAsync(
            Key,
            run.Id,
            [new PushedFix(0, run.Id, "k1", "abcdef123456", "fix(src): change", null, Pushed: false, AiDrafted: false, ReplyPosted: false, now)],
            CancellationToken.None);

        await _Store.AbandonPushedFixesAsync(Key, run.Id, CancellationToken.None);

        Assert.Empty(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));
        // A rejected push leaves nothing to confirm.
        await _Store.ConfirmPushedFixesAsync(Key, run.Id, CancellationToken.None);
        Assert.Empty(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));

        // Confirmed rows survive abandon (a second rejection must not erase a pushed fix).
        await _Store.SavePushedFixesAsync(
            Key,
            run.Id,
            [new PushedFix(0, run.Id, "k2", "abcdef123456", "fix(src): change", null, Pushed: false, AiDrafted: false, ReplyPosted: false, now)],
            CancellationToken.None);
        await _Store.ConfirmPushedFixesAsync(Key, run.Id, CancellationToken.None);
        await _Store.AbandonPushedFixesAsync(Key, run.Id, CancellationToken.None);
        Assert.Single(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Prune_deletes_pushed_fixes_with_their_run()
    {
        var now = DateTimeOffset.UtcNow;
        var oldRun = Run("old", now.AddDays(-10), success: false, "old-key");
        var latestRun = Run("latest", now, success: true, "latest-key");
        await _Store.SaveRunAsync(oldRun, CancellationToken.None);
        await _Store.SaveRunAsync(latestRun, CancellationToken.None);
        await _Store.SavePushedFixesAsync(
            Key,
            oldRun.Id,
            [new PushedFix(0, oldRun.Id, "old-key", "deadbeef", "fix: old", null, false, false, false, now.AddDays(-10))],
            CancellationToken.None);

        var pruned = await _Store.PruneAsync(now.AddDays(-1), minRunsPerPr: 1, CancellationToken.None);

        Assert.Equal(1, pruned);
        Assert.Empty(await _Store.GetUnrepliedPushedFixesAsync(Key, CancellationToken.None));
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
    [Fact]
    public async Task PruneAsync_returns_zero_when_nothing_is_prunable()
    {
        var pruned = await _Store.PruneAsync(DateTimeOffset.UtcNow, minRunsPerPr: 1, CancellationToken.None);

        Assert.Equal(0, pruned);
        Assert.Empty(await _Store.GetRecentRunsAsync(Key, 10, CancellationToken.None));
    }
    [Fact]
    public async Task Resolve_actions_round_trip_and_reply_state_persist()
    {
        var runId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var action = new ResolveAction(0, runId, 42, TriageVerdict.Actionable,
            ResolutionOutcome.Fixed, "abc123", false, created, "Fixed in abc123.");

        await _Store.SaveResolveActionsAsync(Key, runId, [action], CancellationToken.None);

        var loaded = Assert.Single(await _Store.GetResolveActionsAsync(Key, [42], CancellationToken.None));
        Assert.Equal(runId, loaded.RunId);
        Assert.Equal(42, loaded.ThreadId);
        Assert.Equal(TriageVerdict.Actionable, loaded.Verdict);
        Assert.Equal("Fixed in abc123.", loaded.ReplyText);
        Assert.Equal(ResolutionOutcome.Fixed, loaded.Outcome);
        Assert.Equal("abc123", loaded.CommitSha);
        Assert.False(loaded.ReplyPosted);
        Assert.Equal(created, loaded.CreatedAt);

        await _Store.MarkResolveActionRepliedAsync(loaded.Id, CancellationToken.None);
        var replied = Assert.Single(await _Store.GetResolveActionsAsync(Key, [42], CancellationToken.None));
        Assert.True(replied.ReplyPosted);
    }

    [Fact]
    public async Task Saving_a_new_comment_action_resets_previous_reply_state_and_watermark()
    {
        var firstRun = Guid.NewGuid();
        var first = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await _Store.SaveResolveActionsAsync(Key, firstRun,
            [new ResolveAction(0, firstRun, 42, TriageVerdict.Question, ResolutionOutcome.Question, null, false, first)],
            CancellationToken.None);
        var original = Assert.Single(await _Store.GetResolveActionsAsync(Key, [42], CancellationToken.None));
        await _Store.MarkResolveActionRepliedAsync(original.Id, CancellationToken.None);

        var secondRun = Guid.NewGuid();
        var second = first.AddHours(1);
        await _Store.SaveResolveActionsAsync(Key, secondRun,
            [new ResolveAction(0, secondRun, 42, TriageVerdict.Actionable, ResolutionOutcome.Fixed, "def456", false, second)],
            CancellationToken.None);

        var updated = Assert.Single(await _Store.GetResolveActionsAsync(Key, [42], CancellationToken.None));
        Assert.Equal(secondRun, updated.RunId);
        Assert.Equal(second, updated.CreatedAt);
        Assert.False(updated.ReplyPosted);
        Assert.Equal("def456", updated.CommitSha);
    }

    [Fact]
    public async Task Ctor_adds_reply_text_to_an_existing_resolve_actions_table()
    {
        await using (var connection = new SqliteConnection(_ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE ResolveActions DROP COLUMN ReplyText";
            await command.ExecuteNonQueryAsync();
        }

        var migrated = new SqliteFindingStore(_ConnectionString);
        var runId = Guid.NewGuid();
        await migrated.SaveResolveActionsAsync(Key, runId,
            [new ResolveAction(0, runId, 91, TriageVerdict.Question, ResolutionOutcome.Question,
                null, false, DateTimeOffset.UtcNow, "clarify")], CancellationToken.None);

        var loaded = Assert.Single(await migrated.GetResolveActionsAsync(Key, [91], CancellationToken.None));
        Assert.Equal("clarify", loaded.ReplyText);
    }

    [Fact]
    public async Task Last_completed_resolve_run_returns_latest_successful_watermark_only()
    {
        var first = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(1);
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "resolve-1", ReviewKind.Full,
            first.AddMinutes(-5), first, true, [], first.AddMinutes(-1), "Resolve"), CancellationToken.None);
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "failed", ReviewKind.Full,
            second.AddMinutes(-5), second, false, [], second, "Resolve"), CancellationToken.None);
        await _Store.SaveRunAsync(new ReviewRun(Guid.NewGuid(), Key, "review", ReviewKind.Full,
            second.AddMinutes(1), second.AddMinutes(2), true, [], second.AddMinutes(2), "Review"), CancellationToken.None);

        var loaded = await _Store.GetLastCompletedResolveRunAsync(Key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("resolve-1", loaded.HeadSha);
        Assert.Equal("Resolve", loaded.Pipeline);
        Assert.Equal(first.AddMinutes(-1), loaded.LastObservedCommentAt);
    }

}
