using System.ComponentModel.DataAnnotations;

namespace ReviewForge.Service;

/// <summary>Finding-store and ingest-queue persistence settings.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>SQLite connection string for the finding store and optional durable queue.</summary>
    public string StoreConnectionString { get; init; } = "Data Source=reviewforge.db";

    /// <summary>Ingest queue backing: Memory (default) or Sqlite (durable rows on the store's database file).</summary>
    public string QueueMode { get; init; } = nameof(Queue.QueueMode.Memory);

    /// <summary>Wal (default) | Delete. WAL requires POSIX advisory locks; use Delete on network filesystems.</summary>
    public string JournalMode { get; init; } = "Wal";

    public RetentionOptions Retention { get; init; } = new();
}

/// <summary>Bounds finding-store growth while preserving dedupe continuity.</summary>
public sealed class RetentionOptions
{
    /// <summary>Runs started earlier than this many days are pruned, subject to per-PR retention.</summary>
    [Range(1, 3650)]
    public int Days { get; init; } = 30;

    /// <summary>Minimum number of runs retained per pull request regardless of age.</summary>
    [Range(1, 500)]
    public int MinRunsPerPr { get; init; } = 5;
}