using Microsoft.EntityFrameworkCore;

namespace ReviewForge.Infrastructure.Persistence;

public sealed class RunEntity
{
    public Guid Id { get; set; }
    public required string Org { get; set; }
    public required string Project { get; set; }
    public required string RepositoryId { get; set; }
    public int PrId { get; set; }
    public required string HeadSha { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Newest observed comment timestamp (ADO server time) at fetch; the follow-up
    /// gate compares against this instead of the local-clock CompletedAt (P2-24).</summary>
    public DateTimeOffset? LastObservedCommentAt { get; set; }

    /// <summary>False rows exist once failed runs are persisted (P1-4 failure backoff);
    /// excluded from PriorRun, backoff eligibility is computed from them.</summary>
    public bool Success { get; set; }
    public List<FindingEntity> Findings { get; set; } = [];
}

public sealed class FindingEntity
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public required string DedupeKey { get; set; }
    public required string RuleId { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public string? FilePath { get; set; }
    public int? Line { get; set; }
    public int? ThreadId { get; set; }
}

public sealed class FindingStoreDbContext(DbContextOptions<FindingStoreDbContext> options) : DbContext(options)
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new {r.Org, r.Project, r.RepositoryId, r.PrId});
            e.HasMany(r => r.Findings).WithOne().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FindingEntity>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.DedupeKey);
        });
    }
}