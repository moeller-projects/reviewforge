using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed finding store. Schema via EnsureCreated — no migrations needed.
/// WAL journal mode plus a per-connection busy_timeout (both applied by
/// <see cref="SqliteConnectionPragmasInterceptor"/> on every opened connection) let
/// multiple workers read and write the same database without SQLITE_BUSY failures.
/// Use <see cref="StoreJournalMode.Delete"/> on filesystems without POSIX advisory locks.
/// </summary>
public sealed class SqliteFindingStore : IFindingStore
{
    private readonly DbContextOptions<FindingStoreDbContext> _Options;

    public SqliteFindingStore(string connectionString, StoreJournalMode journalMode = StoreJournalMode.Wal)
    {
        _Options = new DbContextOptionsBuilder<FindingStoreDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqliteConnectionPragmasInterceptor(journalMode: journalMode))
            .Options;

        using var db = CreateContext();

        // The shared database file may already exist with only the queue schema (durable
        // queue mode) or be brand new. EnsureCreated() is a no-op once ANY table exists,
        // so create the store schema explicitly whenever the Runs table is missing.
        var connection = (SqliteConnection) db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Runs'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = db.Database.GenerateCreateScript();
                cmd.ExecuteNonQuery();
            }

            // EnsureCreated never alters existing tables. Guard every additive schema change
            // so databases created by earlier versions remain readable.
            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'Pipeline'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE Runs ADD COLUMN Pipeline TEXT NOT NULL DEFAULT 'Review'";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'LastObservedCommentAt'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE Runs ADD COLUMN LastObservedCommentAt TEXT NULL";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Findings') WHERE name = 'AppliedFixJson'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE Findings ADD COLUMN AppliedFixJson TEXT NULL";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Findings') WHERE name = 'FindingJson'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE Findings ADD COLUMN FindingJson TEXT NULL";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Findings') WHERE name = 'Published'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE Findings ADD COLUMN Published INTEGER NOT NULL DEFAULT 0";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS \"ResolveActions\" (" +
                "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ResolveActions\" PRIMARY KEY AUTOINCREMENT, " +
                "\"RunId\" TEXT NOT NULL, \"Org\" TEXT NOT NULL, \"Project\" TEXT NOT NULL, " +
                "\"RepositoryId\" TEXT NOT NULL, \"PrId\" INTEGER NOT NULL, \"ThreadId\" INTEGER NOT NULL, " +
                "\"Verdict\" TEXT NOT NULL, \"Outcome\" TEXT NOT NULL, \"CommitSha\" TEXT NULL, " +
                "\"ReplyPosted\" INTEGER NOT NULL, \"CreatedAt\" TEXT NOT NULL, \"ReplyText\" TEXT NULL); " +
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ResolveActions_Pr_Thread\" ON " +
                "\"ResolveActions\" (\"Org\", \"Project\", \"RepositoryId\", \"PrId\", \"ThreadId\");";
            cmd.ExecuteNonQuery();
            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('ResolveActions') WHERE name = 'ReplyText'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE ResolveActions ADD COLUMN ReplyText TEXT NULL";
                cmd.ExecuteNonQuery();
            }

            // Databases created before CommitOnHead have no PushedFixes table at all —
            // GenerateCreateScript only runs for brand-new databases.
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS \"PushedFixes\" (" +
                "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_PushedFixes\" PRIMARY KEY AUTOINCREMENT, " +
                "\"RunId\" TEXT NOT NULL, \"Org\" TEXT NOT NULL, \"Project\" TEXT NOT NULL, " +
                "\"RepositoryId\" TEXT NOT NULL, \"PrId\" INTEGER NOT NULL, " +
                "\"DedupeKey\" TEXT NOT NULL, \"CommitSha\" TEXT NOT NULL, \"CommitSubject\" TEXT NOT NULL, " +
                "\"ThreadId\" INTEGER NULL, \"Pushed\" INTEGER NOT NULL DEFAULT 1, " +
                "\"AiDrafted\" INTEGER NOT NULL DEFAULT 0, \"ReplyPosted\" INTEGER NOT NULL, " +
                "\"CreatedAt\" TEXT NOT NULL); " +
                "CREATE INDEX IF NOT EXISTS \"IX_PushedFixes_Org_Project_RepositoryId_PrId\" ON " +
                "\"PushedFixes\" (\"Org\", \"Project\", \"RepositoryId\", \"PrId\");";
            cmd.ExecuteNonQuery();

            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('PushedFixes') WHERE name = 'Pushed'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                // Pre-two-phase rows were written only after a successful push — Pushed=1
                // preserves their reconciliation eligibility.
                cmd.CommandText = "ALTER TABLE PushedFixes ADD COLUMN Pushed INTEGER NOT NULL DEFAULT 1";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('PushedFixes') WHERE name = 'AiDrafted'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText = "ALTER TABLE PushedFixes ADD COLUMN AiDrafted INTEGER NOT NULL DEFAULT 0";
                cmd.ExecuteNonQuery();
                // Commanded (thread-keyed) fixes of earlier builds were always AI-drafted.
                cmd.CommandText = "UPDATE PushedFixes SET AiDrafted = 1 WHERE DedupeKey LIKE 'thread-%'";
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
            "AND Pipeline = 'Review' AND CompletedAt IS NOT NULL AND Success = 1 " +
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
            [
                .. run.Findings
                    .Where(f => !f.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix, StringComparison.Ordinal))
                    .Select(f => f.DedupeKey)
            ],
            [
                .. run.Findings.Select(f => new StoredFinding(
                    f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId, f.AppliedFixJson,
                    f.FindingJson, f.Published))
            ],
            run.LastObservedCommentAt);
    }

    public async Task SavePushedFixesAsync(PrKey pr, Guid runId, IReadOnlyList<PushedFix> fixes, CancellationToken ct)
    {
        await using var db = CreateContext();
        foreach (var fix in fixes)
        {
            db.PushedFixes.Add(new PushedFixEntity
            {
                RunId = runId,
                Org = pr.Org,
                Project = pr.Project,
                RepositoryId = pr.RepositoryId,
                PrId = pr.PrId,
                DedupeKey = fix.DedupeKey,
                CommitSha = fix.CommitSha,
                CommitSubject = fix.CommitSubject,
                ThreadId = fix.ThreadId,
                Pushed = false,
                AiDrafted = fix.AiDrafted,
                ReplyPosted = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ConfirmPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.PushedFixes
            .Where(p => p.Org == pr.Org && p.Project == pr.Project && p.RepositoryId == pr.RepositoryId
                        && p.PrId == pr.PrId && p.RunId == runId && !p.Pushed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Pushed, true), ct)
            .ConfigureAwait(false);
    }

    public async Task AbandonPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.PushedFixes
            .Where(p => p.Org == pr.Org && p.Project == pr.Project && p.RepositoryId == pr.RepositoryId
                        && p.PrId == pr.PrId && p.RunId == runId && !p.Pushed)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PushedFix>> GetUnrepliedPushedFixesAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        var rows = await db.PushedFixes
            .Where(p => p.Org == pr.Org && p.Project == pr.Project && p.RepositoryId == pr.RepositoryId
                        && p.PrId == pr.PrId && p.Pushed && !p.ReplyPosted)
            .OrderBy(p => p.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return
        [
            .. rows.Select(p => new PushedFix(
                p.Id, p.RunId, p.DedupeKey, p.CommitSha, p.CommitSubject, p.ThreadId, p.Pushed, p.AiDrafted, p.ReplyPosted, p.CreatedAt))
        ];
    }

    public async Task MarkPushedFixRepliedAsync(int pushedFixId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.PushedFixes
            .Where(p => p.Id == pushedFixId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReplyPosted, true), ct)
            .ConfigureAwait(false);
    }

    /// <summary>The run row by id regardless of outcome, or null when unknown.</summary>
    public async Task<ReviewRun?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        await using var db = CreateContext();
        var run = await db.Runs
            .Include(r => r.Findings)
            .FirstOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        return new ReviewRun(
            run.Id,
            new PrKey(run.Org, run.Project, run.RepositoryId, run.PrId),
            run.HeadSha,
            Enum.Parse<ReviewKind>(run.Kind, ignoreCase: true),
            run.StartedAt,
            run.CompletedAt,
            run.Success,
            [
                .. run.Findings.Select(f => new StoredFinding(
                    f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId, f.AppliedFixJson,
                    f.FindingJson, f.Published))
            ],
            run.LastObservedCommentAt,
            run.Pipeline);
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

    public async Task<ReviewRun?> GetLastCompletedResolveRunAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        // SQLite cannot order DateTimeOffset values; filter in SQL and order the per-PR candidates in memory.
        var candidates = await db.Runs
            .AsNoTracking()
            .Where(r => r.Org == pr.Org && r.Project == pr.Project && r.RepositoryId == pr.RepositoryId
                        && r.PrId == pr.PrId && r.Pipeline == "Resolve"
                        && r.CompletedAt != null && r.Success)
            .ToListAsync(ct).ConfigureAwait(false);
        var run = candidates.OrderByDescending(r => r.StartedAt).FirstOrDefault();
        return run is null
            ? null
            : new ReviewRun(run.Id, pr, run.HeadSha, Enum.Parse<ReviewKind>(run.Kind),
                run.StartedAt, run.CompletedAt, run.Success, [], run.LastObservedCommentAt, run.Pipeline);
    }

    public async Task<IReadOnlyList<ResolveAction>> GetResolveActionsAsync(
        PrKey pr, IReadOnlyCollection<int> threadIds, CancellationToken ct)
    {
        await using var db = CreateContext();
        var query = db.ResolveActions.Where(a => a.Org == pr.Org && a.Project == pr.Project
                                                                 && a.RepositoryId == pr.RepositoryId && a.PrId == pr.PrId);
        if (threadIds.Count > 0)
        {
            query = query.Where(a => threadIds.Contains(a.ThreadId));
        }

        var rows = await query.OrderBy(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        return
        [
            .. rows.Select(a => new ResolveAction(a.Id, a.RunId, a.ThreadId,
                Enum.Parse<TriageVerdict>(a.Verdict), Enum.Parse<ResolutionOutcome>(a.Outcome),
                a.CommitSha, a.ReplyPosted, a.CreatedAt, a.ReplyText))
        ];
    }

    public async Task SaveResolveActionsAsync(
        PrKey pr, Guid runId, IReadOnlyList<ResolveAction> actions, CancellationToken ct)
    {
        await using var db = CreateContext();
        foreach (var action in actions)
        {
            var existing = await db.ResolveActions.FirstOrDefaultAsync(a =>
                a.Org == pr.Org && a.Project == pr.Project && a.RepositoryId == pr.RepositoryId
                && a.PrId == pr.PrId && a.ThreadId == action.ThreadId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.ResolveActions.Add(new ResolveActionEntity
                {
                    RunId = runId, Org = pr.Org, Project = pr.Project, RepositoryId = pr.RepositoryId,
                    PrId = pr.PrId, ThreadId = action.ThreadId, Verdict = action.Verdict.ToString(),
                    Outcome = action.Outcome.ToString(), CommitSha = action.CommitSha, ReplyPosted = false,
                    CreatedAt = action.CreatedAt == default ? DateTimeOffset.UtcNow : action.CreatedAt,
                    ReplyText = action.ReplyText
                });
            }
            else
            {
                existing.RunId = runId;
                existing.Verdict = action.Verdict.ToString();
                existing.Outcome = action.Outcome.ToString();
                existing.CommitSha = action.CommitSha;
                existing.ReplyPosted = false;
                existing.CreatedAt = action.CreatedAt == default ? DateTimeOffset.UtcNow : action.CreatedAt;
                existing.ReplyText = action.ReplyText;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkResolveActionRepliedAsync(int id, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.ResolveActions.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReplyPosted, true), ct)
            .ConfigureAwait(false);
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
                .Where(f => !f.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix))
                .Where(f => db.Runs.Any(r => r.Id == f.RunId
                                             && r.Org == pr.Org && r.Project == pr.Project
                                             && r.RepositoryId == pr.RepositoryId && r.PrId == pr.PrId))
                .Select(f => f.DedupeKey)
                .Distinct()
                .ToListAsync(ct)
        ];
    }

    public async Task<IReadOnlySet<long>> GetCommandedFixThreadIdsAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        var keys = await db.Findings
            .Where(f => f.DedupeKey.StartsWith(AppliedFix.CommandKeyPrefix))
            .Where(f => db.Runs.Any(r => r.Id == f.RunId
                                         && r.Org == pr.Org && r.Project == pr.Project
                                         && r.RepositoryId == pr.RepositoryId && r.PrId == pr.PrId))
            .Select(f => f.DedupeKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return keys
            .Select(key => key[AppliedFix.CommandKeyPrefix.Length..])
            .Select(suffix => long.TryParse(suffix, out var id) ? (long?) id : null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
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
                Pipeline = run.Pipeline,
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
            existing.Pipeline = run.Pipeline;
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
        AppliedFixJson = f.AppliedFixJson,
        FindingJson = f.FindingJson,
        Published = f.Published,
    };

    public async Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Findings
            .Where(f => f.RunId == runId && f.DedupeKey == dedupeKey)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ThreadId, threadId), ct);
    }

    public async Task MarkFindingPublishedAsync(Guid runId, string dedupeKey, int? threadId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Findings.Where(f => f.RunId == runId && f.DedupeKey == dedupeKey)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Published, true)
                .SetProperty(f => f.ThreadId, f => threadId ?? f.ThreadId), ct).ConfigureAwait(false);
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
                    [
                        .. r.Findings.Select(f => new StoredFinding(
                            f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId,
                            f.AppliedFixJson, f.FindingJson, f.Published))
                    ],
                    r.LastObservedCommentAt, r.Pipeline))
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
                    [], r.LastObservedCommentAt, r.Pipeline))
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
        var connection = (SqliteConnection) db.Database.GetDbConnection();
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

            await using (var deletePushedFixes = connection.CreateCommand())
            {
                deletePushedFixes.Transaction = tx;
                deletePushedFixes.CommandText = PrunableRunsCte + "DELETE FROM PushedFixes WHERE RunId IN (SELECT Id FROM Prunable)";
                AddPruneParameters(deletePushedFixes, olderThan, minRunsPerPr);
                await deletePushedFixes.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var deleteResolveActions = connection.CreateCommand())
            {
                deleteResolveActions.Transaction = tx;
                deleteResolveActions.CommandText =
                    PrunableRunsCte + "DELETE FROM ResolveActions WHERE RunId IN (SELECT Id FROM Prunable)";
                AddPruneParameters(deleteResolveActions, olderThan, minRunsPerPr);
                await deleteResolveActions.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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