using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>
/// Applies <c>PRAGMA busy_timeout</c> to every opened connection. With N pipeline workers
/// sharing one SQLite file, a contended write must wait instead of failing with SQLITE_BUSY.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor(int milliseconds = 5000) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyBusyTimeout(connection, milliseconds);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={milliseconds}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void ApplyBusyTimeout(DbConnection connection, int milliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={milliseconds}";
        command.ExecuteNonQuery();
    }
}
