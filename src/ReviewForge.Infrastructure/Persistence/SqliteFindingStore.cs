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

        // EnsureCreated never alters existing tables; upgrade pre-watermark databases
        // in place so the gate's server-time comparison works on old data files.
        var hasWatermarkColumn = false;
        using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Runs') WHERE name = 'LastObservedCommentAt'";
            db.Database.OpenConnection();
            try
            {
                hasWatermarkColumn = Convert.ToInt32(cmd.ExecuteScalar()) == 1;
            }
            finally
            {
                db.Database.CloseConnection();
            }
        }

        if (!hasWatermarkColumn)
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE Runs ADD COLUMN LastObservedCommentAt TEXT NULL");
        }
    }

    public async Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct)
    {
        await using var db = CreateContext();
        // SQLite cannot ORDER BY DateTimeOffset — filter in SQL, pick the latest in memory (few runs per PR).
        var runs = await db.Runs
            .Include(r => r.Findings)
            .Where(r => r.Org == pr.Org && r.Project == pr.Project
                                        && r.RepositoryId == pr.RepositoryId && r.PrId == pr.PrId
                                        && r.CompletedAt != null && r.Success)
            .ToListAsync(ct);

        var run = runs.MaxBy(r => r.CompletedAt);
        return run is null
            ? null
            : new PriorRun(
                pr,
                run.HeadSha,
                run.CompletedAt!.Value,
                [.. run.Findings.Select(f => f.DedupeKey)],
                [.. run.Findings.Select(f => new StoredFinding(
                    f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId))],
                run.LastObservedCommentAt);
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
        var runs = await db.Runs
            .Include(r => r.Findings)
            .Where(r => r.Org == pr.Org && r.Project == pr.Project
                                        && r.RepositoryId == pr.RepositoryId && r.PrId == pr.PrId)
            .ToListAsync(ct);

        // SQLite cannot ORDER BY DateTimeOffset — order in memory (few runs per PR).
        return
        [
            .. runs
                .OrderByDescending(r => r.StartedAt)
                .Take(count)
                .Select(r => new ReviewRun(
                    r.Id, pr, r.HeadSha, Enum.Parse<ReviewKind>(r.Kind),
                    r.StartedAt, r.CompletedAt, r.Success,
                    [.. r.Findings.Select(f => new StoredFinding(f.DedupeKey, f.RuleId, f.Severity, f.Title, f.FilePath, f.Line, f.ThreadId))],
                    r.LastObservedCommentAt))
        ];
    }

    private FindingStoreDbContext CreateContext() => new(_Options);
}