using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>
/// Applies the store's per-connection pragmas on every opened connection. busy_timeout:
/// with N pipeline workers sharing one SQLite file, a contended write waits instead of
/// failing with SQLITE_BUSY. WAL mode additionally sets journal_mode=WAL (persistent in
/// the file; a near-free no-op on later opens) and synchronous=NORMAL — safe with WAL,
/// where a power loss can lose the last transaction but never corrupt the file. That is
/// acceptable for a dedupe/audit store whose worst case is one re-review. Delete mode
/// keeps the rollback journal with FULL durability for filesystems without POSIX
/// advisory locks.
/// </summary>
internal sealed class SqliteConnectionPragmasInterceptor(
    int busyTimeoutMilliseconds = 5000,
    StoreJournalMode journalMode = StoreJournalMode.Wal) : DbConnectionInterceptor
{
    private readonly string _Pragmas = journalMode == StoreJournalMode.Wal
        ? $"PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL"
        : $"PRAGMA busy_timeout={busyTimeoutMilliseconds}; PRAGMA journal_mode=DELETE";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Applies the configured pragma set to an already-open connection. Test seam;
    /// production traffic flows through the ConnectionOpened overrides.</summary>
    internal void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _Pragmas;
        command.ExecuteNonQuery();
    }
}
