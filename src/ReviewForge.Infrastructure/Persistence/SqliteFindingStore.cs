using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>SQLite-backed finding store. Schema via EnsureCreated — single-writer service, no migrations needed.</summary>
public sealed class SqliteFindingStore : IFindingStore
{
    private readonly DbContextOptions<FindingStoreDbContext> _Options;

    public SqliteFindingStore(string connectionString)
    {
        _Options = new DbContextOptionsBuilder<FindingStoreDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var db = CreateContext();
        db.Database.EnsureCreated();
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
            : new PriorRun(pr, run.HeadSha, run.CompletedAt!.Value, [.. run.Findings.Select(f => f.DedupeKey)]);
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
            Success = run.Success,
            Findings =
            [
                .. run.Findings.Select(f => new FindingEntity
                {
                    RunId = run.Id,
                    DedupeKey = f.DedupeKey,
                    RuleId = f.RuleId,
                    Severity = f.Severity,
                    Title = f.Title,
                    FilePath = f.FilePath,
                    Line = f.Line,
                    ThreadId = f.ThreadId,
                })
            ],
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Findings
            .Where(f => f.RunId == runId && f.DedupeKey == dedupeKey)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ThreadId, threadId), ct);
    }

    private FindingStoreDbContext CreateContext() => new(_Options);
}