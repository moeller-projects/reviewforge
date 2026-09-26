using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed finding store. Schema via EnsureCreated — no migrations needed.
/// WAL journal mode (set once, persists in the file) plus a per-connection busy_timeout
/// let multiple workers read and write the same database without SQLITE_BUSY failures.
/// </summary>
public sealed class SqliteFindingStore : IFindingStore
{
    private readonly DbContextOptions<FindingStoreDbContext> _Options;

    public SqliteFindingStore(string connectionString)
    {
        _Options = new DbContextOptionsBuilder<FindingStoreDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqliteBusyTimeoutInterceptor())
            .Options;

        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL");

        // EnsureCreated never alters existing tables. An immediate SQLite transaction
        // serializes the check-and-alter sequence across concurrently starting instances.
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'LastObservedCommentAt'";
            var hasWatermarkColumn = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
            if (!hasWatermarkColumn)
            {
                cmd.CommandText = "ALTER TABLE Runs ADD COLUMN LastObservedCommentAt TEXT NULL";
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    public async Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        // Single-row lookup (P2-26): runs of one PR never overlap (in-flight claims), so
        // rowid order is completion order. Findings load in a second query — never the
        // whole run history.
        var id = await QuerySingleRunId(db,
            "SELECT Id FROM Runs " +
            "WHERE Org = $org AND Project = $project AND RepositoryId = $repo AND PrId = $prId " +
            "AND CompletedAt IS NOT NULL AND Success = 1 " +
            "ORDER BY rowid DESC LIMIT 1",
            pr, ct).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        var run = await db.Runs
            .Include(r => r.Findings)
            .FirstAsync(r => r.Id == id.Value, ct);
        return new PriorRun(
            pr,
            run.HeadSha,
            run.CompletedAt!.Value,
            [.. run.Findings.Select(f => f.DedupeKey)],
            [.. run.Findings.Select(f => new StoredFinding(
                f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId))],
            run.LastObservedCommentAt);
    }

    /// <summary>Runs one of the bounded rowid-ordered id queries against <paramref name="sql"/>.</summary>
    private static async Task<Guid?> QuerySingleRunId(
        FindingStoreDbContext db, string sql, PrKey pr, CancellationToken ct)
    {
        var ids = await QueryRunIds(db, sql, pr, limit: null, ct).ConfigureAwait(false);
        return ids.Count > 0 ? ids[0] : null;
    }

    private static async Task<List<Guid>> QueryRunIds(
        FindingStoreDbContext db, string sql, PrKey pr, int? limit, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new SqliteParameter("$org", pr.Org));
            cmd.Parameters.Add(new SqliteParameter("$project", pr.Project));
            cmd.Parameters.Add(new SqliteParameter("$repo", pr.RepositoryId));
            cmd.Parameters.Add(new SqliteParameter("$prId", pr.PrId));
            if (limit is not null)
            {
                cmd.Parameters.Add(new SqliteParameter("$limit", limit.Value));
            }

            var ids = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var db = CreateContext();
        if (!await db.Database.CanConnectAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQLite database connection failed.");
        }
    }

    public async Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        return
        [
            .. await db.Findings
                .Where(f => db.Runs.Any(r => r.Id == f.RunId
                                             && r.Org == pr.Org && r.Project == pr.Project
                                             && r.RepositoryId == pr.RepositoryId && r.PrId == pr.PrId))
                .Select(f => f.DedupeKey)
                .Distinct()
                .ToListAsync(ct)
        ];
    }

    public async Task SaveRunAsync(ReviewRun run, CancellationToken ct)
    {
        await using var db = CreateContext();
        var existing = await db.Runs
            .Include(r => r.Findings)
            .FirstOrDefaultAsync(r => r.Id == run.Id, ct);

        if (existing is null)
        {
            db.Runs.Add(new RunEntity
            {
                Id = run.Id,
                Org = run.Pr.Org,
                Project = run.Pr.Project,
                RepositoryId = run.Pr.RepositoryId,
                PrId = run.Pr.PrId,
                HeadSha = run.HeadSha,
                Kind = run.Kind.ToString(),
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt,
                LastObservedCommentAt = run.LastObservedCommentAt,
                Success = run.Success,
                Findings = [.. run.Findings.Select(f => ToEntity(f, run.Id))],
            });
        }
        else
        {
            // Finalize an in-flight run: update completion, merge newly relevant finding rows.
            // Existing rows keep their ThreadId backfills (SetThreadIdAsync) — never overwritten.
            existing.HeadSha = run.HeadSha;
            existing.Kind = run.Kind.ToString();
            existing.CompletedAt = run.CompletedAt;
            existing.LastObservedCommentAt = run.LastObservedCommentAt;
            existing.Success = run.Success;
            var knownKeys = existing.Findings.Select(f => f.DedupeKey).ToHashSet(StringComparer.Ordinal);
            foreach (var finding in run.Findings.Where(f => !knownKeys.Contains(f.DedupeKey)))
            {
                existing.Findings.Add(ToEntity(finding, run.Id));
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static FindingEntity ToEntity(StoredFinding f, Guid runId) => new()
    {
        RunId = runId,
        DedupeKey = f.DedupeKey,
        RuleId = f.RuleId,
        Severity = f.Severity,
        Title = f.Title,
        FilePath = f.FilePath,
        Line = f.Line,
        ThreadId = f.ThreadId,
    };

    public async Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Findings
            .Where(f => f.RunId == runId && f.DedupeKey == dedupeKey)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ThreadId, threadId), ct);
    }

    public async Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct)
    {
        await using var db = CreateContext();
        // Bounded in SQL (P2-26): rowid DESC matches completion order per PR (runs of one
        // PR never overlap), then findings load only for the selected runs.
        var ids = await QueryRunIds(db,
            "SELECT Id FROM Runs " +
            "WHERE Org = $org AND Project = $project AND RepositoryId = $repo AND PrId = $prId " +
            "ORDER BY rowid DESC LIMIT $limit",
            pr, Math.Clamp(count, 0, 500), ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return [];
        }

        var runs = await db.Runs
            .Include(r => r.Findings)
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(ct);

        // Preserve the SQL order; per-PR runs never overlap, so this matches the previous
        // OrderByDescending(StartedAt) semantics exactly.
        return
        [
            .. runs
                .OrderBy(r => ids.IndexOf(r.Id))
                .Select(r => new ReviewRun(
                    r.Id, pr, r.HeadSha, Enum.Parse<ReviewKind>(r.Kind),
                    r.StartedAt, r.CompletedAt, r.Success,
                    [.. r.Findings.Select(f => new StoredFinding(f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId))],
                    r.LastObservedCommentAt))
        ];
    }

    public async Task<IReadOnlyList<ReviewRun>> GetStaleShellsAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var db = CreateContext();
        // SQLite cannot translate DateTimeOffset inequality — filter null completion in SQL,
        // compare StartedAt in memory (the shell set is tiny).
        var runs = await db.Runs
            .Where(r => r.CompletedAt == null)
            .ToListAsync(ct);

        // Reaper input only — findings are not loaded; SaveRunAsync upsert preserves rows.
        return
        [
            .. runs
                .Where(r => r.StartedAt < olderThan)
                .Select(r => new ReviewRun(
                    r.Id, new PrKey(r.Org, r.Project, r.RepositoryId, r.PrId), r.HeadSha,
                    Enum.Parse<ReviewKind>(r.Kind), r.StartedAt, r.CompletedAt, r.Success,
                    [], r.LastObservedCommentAt))
        ];
    }

    private const string PrunableRunsCte =
        "WITH Prunable AS (" +
        "SELECT r.Id FROM Runs r " +
        "WHERE r.StartedAt < $olderThan " +
        // Never the latest completed run of a PR — dedupe continuity depends on it.
        "AND r.Id NOT IN (" +
        "SELECT r2.Id FROM Runs r2 " +
        "WHERE r2.Org = r.Org AND r2.Project = r.Project AND r2.RepositoryId = r.RepositoryId AND r2.PrId = r.PrId " +
        "AND r2.CompletedAt IS NOT NULL AND r2.Success = 1 " +
        "ORDER BY r2.rowid DESC LIMIT 1) " +
        // Always keep at least the last minKeep runs of a PR regardless of age.
        "AND r.Id NOT IN (" +
        "SELECT r3.Id FROM Runs r3 " +
        "WHERE r3.Org = r.Org AND r3.Project = r.Project AND r3.RepositoryId = r.RepositoryId AND r3.PrId = r.PrId " +
        "ORDER BY r3.rowid DESC LIMIT $minKeep)) ";

    public async Task<int> PruneAsync(DateTimeOffset olderThan, int minRunsPerPr, CancellationToken ct)
    {
        await using var db = CreateContext();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            // Immediate transaction serializes the check-and-delete across concurrent pruners,
            // mirroring the guarded ALTER pattern in the constructor.
            await using var tx = connection.BeginTransaction(deferred: false);
            await using (var deleteFindings = connection.CreateCommand())
            {
                deleteFindings.Transaction = tx;
                deleteFindings.CommandText = PrunableRunsCte + "DELETE FROM Findings WHERE RunId IN (SELECT Id FROM Prunable)";
                AddPruneParameters(deleteFindings, olderThan, minRunsPerPr);
                await deleteFindings.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var pruned = 0;
            await using (var deleteRuns = connection.CreateCommand())
            {
                deleteRuns.Transaction = tx;
                deleteRuns.CommandText = PrunableRunsCte + "DELETE FROM Runs WHERE Id IN (SELECT Id FROM Prunable) RETURNING Id";
                AddPruneParameters(deleteRuns, olderThan, minRunsPerPr);
                await using var reader = await deleteRuns.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    pruned++;
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return pruned;
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static void AddPruneParameters(SqliteCommand command, DateTimeOffset olderThan, int minRunsPerPr)
    {
        command.Parameters.Add(new SqliteParameter("$olderThan", olderThan));
        command.Parameters.Add(new SqliteParameter("$minKeep", minRunsPerPr));
    }

    private FindingStoreDbContext CreateContext() => new(_Options);
}