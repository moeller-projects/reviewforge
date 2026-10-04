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
    /// <summary>Pipeline which produced the run (for example Review or Resolve).</summary>
    public required string Pipeline { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Newest observed comment timestamp (ADO server time) at fetch; the follow-up
    /// gate compares against this instead of the local-clock CompletedAt (P2-24).</summary>
    public DateTimeOffset? LastObservedCommentAt { get; set; }
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

    /// <summary>Serialized AppliedFix for fixes applied on that run; null for plain findings.</summary>
    public string? AppliedFixJson { get; set; }
}

/// <summary>Durable resolve action and its reply state.</summary>
public sealed class ResolveActionEntity
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public required string Org { get; set; }
    public required string Project { get; set; }
    public required string RepositoryId { get; set; }
    public int PrId { get; set; }
    public int ThreadId { get; set; }
    public required string Verdict { get; set; }
    public required string Outcome { get; set; }
    public string? CommitSha { get; set; }
    public bool ReplyPosted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReplyText { get; set; }
}


/// <summary>Durable pushed-fix record (CommitOnHead): written by the commit stage as a push
/// INTENT before the external push and confirmed atomically after it succeeds — the
/// crash-before/after-push recovery source. Only confirmed rows are reconciled for replies.
/// Rows are pruned with their run row.</summary>
public sealed class PushedFixEntity
{
    public int Id { get; set; }
    public Guid RunId { get; set; }
    public required string Org { get; set; }
    public required string Project { get; set; }
    public required string RepositoryId { get; set; }
    public int PrId { get; set; }
    public required string DedupeKey { get; set; }   // finding key or "thread-{id}"
    public required string CommitSha { get; set; }
    public required string CommitSubject { get; set; }
    public int? ThreadId { get; set; }               // resolved live thread when known
    /// <summary>False while the row is a pre-push intent; true once the push succeeded.</summary>
    public bool Pushed { get; set; }
    public bool ReplyPosted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FindingStoreDbContext(DbContextOptions<FindingStoreDbContext> options) : DbContext(options)
{
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();
    public DbSet<PushedFixEntity> PushedFixes => Set<PushedFixEntity>();
    public DbSet<ResolveActionEntity> ResolveActions => Set<ResolveActionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RunEntity>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new {r.Org, r.Project, r.RepositoryId, r.PrId});
            e.HasIndex(r => new {r.Org, r.Project, r.RepositoryId, r.PrId, r.CompletedAt});
            e.HasMany(r => r.Findings).WithOne().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ResolveActionEntity>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new {a.Org, a.Project, a.RepositoryId, a.PrId, a.ThreadId})
                .IsUnique().HasDatabaseName("IX_ResolveActions_Pr_Thread");
        });

        modelBuilder.Entity<FindingEntity>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.DedupeKey);
        });

        modelBuilder.Entity<PushedFixEntity>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new {p.Org, p.Project, p.RepositoryId, p.PrId});
        });
    }
}