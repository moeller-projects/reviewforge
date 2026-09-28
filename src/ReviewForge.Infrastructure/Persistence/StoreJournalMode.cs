namespace ReviewForge.Infrastructure.Persistence;

/// <summary>SQLite journal mode for the finding store. <see cref="Wal"/> is the default and
/// requires POSIX advisory locks (local disk only). <see cref="Delete"/> is the escape
/// hatch for filesystems without advisory locks (network shares), at the cost of the
/// single-writer stall WAL removes.</summary>
public enum StoreJournalMode
{
    Wal,
    Delete,
}
