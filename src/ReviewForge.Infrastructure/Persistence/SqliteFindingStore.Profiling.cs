using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;

namespace ReviewForge.Infrastructure.Persistence;

public sealed partial class SqliteFindingStore : IModelProfileStore
{
    private const string ModelProfileSchema = """
        CREATE TABLE IF NOT EXISTS ModelProfiles (
            RunId TEXT NOT NULL PRIMARY KEY, ProfileId TEXT NOT NULL,
            ObservedCalls INTEGER NOT NULL, Truncated INTEGER NOT NULL,
            Incomplete INTEGER NOT NULL, Finalized INTEGER NOT NULL,
            CompletedTicks INTEGER NULL, ExpiresTicks INTEGER NULL, Expired INTEGER NOT NULL DEFAULT 0,
            SchemaVersion INTEGER NOT NULL, SummaryJson TEXT NULL);
        CREATE TABLE IF NOT EXISTS ModelCalls (
            RunId TEXT NOT NULL, Sequence INTEGER NOT NULL, CallId TEXT NOT NULL, MetadataJson TEXT NOT NULL,
            PRIMARY KEY (RunId, Sequence), UNIQUE (CallId));
        CREATE INDEX IF NOT EXISTS IX_ModelProfiles_Expiry ON ModelProfiles (Expired, ExpiresTicks);
        """;

    private static DbCommand ProfileCommand(DbConnection connection, DbTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    public async Task<Guid> BeginModelProfileAsync(ModelProfileSnapshot profile, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = ProfileCommand(db.Database.GetDbConnection(), null, """
            INSERT OR IGNORE INTO ModelProfiles (RunId,ProfileId,ObservedCalls,Truncated,Incomplete,Finalized,SchemaVersion)
            VALUES ($run,$profile,0,0,1,0,$schema)
            """, ("$run", profile.RunId.ToString("D")), ("$profile", profile.ProfileId.ToString("D")), ("$schema", profile.SchemaVersion));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var identity = ProfileCommand(db.Database.GetDbConnection(), null,
            "SELECT ProfileId FROM ModelProfiles WHERE RunId=$run", ("$run", profile.RunId.ToString("D")));
        return Guid.Parse((string)(await identity.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    public async Task FinalizeModelProfileAsync(ModelProfileSnapshot profile, DateTimeOffset completedAt,
        DateTimeOffset expiresAt, bool incomplete, CancellationToken ct)
    {
        if (profile.Calls.Count > 10_000) throw new ArgumentOutOfRangeException(nameof(profile));
        if (await BeginModelProfileAsync(profile, ct).ConfigureAwait(false) != profile.ProfileId)
            throw new ArgumentException("Model profile identity mismatch.", nameof(profile));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.GetDbConnection().BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = ProfileCommand(db.Database.GetDbConnection(), transaction, """
            UPDATE ModelProfiles SET ObservedCalls=$observed, Truncated=$truncated, Incomplete=$incomplete,
                Finalized=1, CompletedTicks=$completed, ExpiresTicks=$expires, SummaryJson=$summary
            WHERE RunId=$run AND ProfileId=$profile AND Finalized=0
            """, ("$observed", profile.ObservedCalls), ("$truncated", profile.Truncated),
            ("$incomplete", incomplete || profile.Incomplete || profile.Aggregate?.InterruptedCalls > 0 || profile.Calls.Any(c => c.Outcome == "interrupted")),
            ("$completed", completedAt.UtcTicks), ("$expires", expiresAt.UtcTicks),
            ("$summary", profile.Aggregate is null ? null : JsonSerializer.Serialize(profile.Aggregate)),
            ("$run", profile.RunId.ToString("D")), ("$profile", profile.ProfileId.ToString("D")));
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0)
        {
            foreach (var call in profile.Calls)
            {
                if (call.RunId != profile.RunId || call.ProfileId != profile.ProfileId)
                    throw new ArgumentException("Profile correlation mismatch.", nameof(profile));
                await using var insert = ProfileCommand(db.Database.GetDbConnection(), transaction, """
                    INSERT INTO ModelCalls (RunId,Sequence,CallId,MetadataJson) VALUES ($run,$sequence,$call,$json)
                    """, ("$run", profile.RunId.ToString("D")), ("$sequence", call.Sequence),
                    ("$call", call.CallId.ToString("D")), ("$json", JsonSerializer.Serialize(call)));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<ModelProfilePage> ReadModelProfileAsync(Guid runId, long afterSequence, int count,
        DateTimeOffset now, CancellationToken ct)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        // Read snapshot keeps summary and page consistent with concurrent expiry without reserving the writer lock.
        await using var transaction = ((Microsoft.Data.Sqlite.SqliteConnection)connection).BeginTransaction(deferred: true);
        await using var command = ProfileCommand(connection, transaction, """
            SELECT ProfileId,ObservedCalls,Truncated,Incomplete,Finalized,ExpiresTicks,Expired,SummaryJson FROM ModelProfiles WHERE RunId=$run
            """, ("$run", runId.ToString("D")));
        Guid profileId;
        long observed;
        bool truncated, incomplete, expired, finalized;
        ModelProfileAggregate? aggregate;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new("legacy-unavailable", runId, null, 0, false, false, [], null);
            profileId = Guid.Parse(reader.GetString(0));
            observed = reader.GetInt64(1);
            truncated = reader.GetBoolean(2);
            finalized = reader.GetBoolean(4);
            incomplete = reader.GetBoolean(3) || !finalized;
            expired = reader.GetBoolean(6) || !reader.IsDBNull(5) && reader.GetInt64(5) <= now.UtcTicks;
            aggregate = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<ModelProfileAggregate>(reader.GetString(7));
        }
        if (expired) return new("expired", runId, profileId, observed, truncated, incomplete, [], null, Aggregate: aggregate, Finalized: finalized);
        var calls = new List<ModelCallRecord>();
        await using var details = ProfileCommand(connection, transaction, """
            SELECT MetadataJson FROM ModelCalls WHERE RunId=$run AND Sequence>$after ORDER BY Sequence LIMIT $count
            """, ("$run", runId.ToString("D")), ("$after", afterSequence), ("$count", count + 1));
        await using (var reader = await details.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) calls.Add(JsonSerializer.Deserialize<ModelCallRecord>(reader.GetString(0))!);
        var more = calls.Count > count;
        if (more) calls.RemoveAt(calls.Count - 1);
        return new(incomplete ? "incomplete" : truncated ? "truncated" : observed == 0 ? "empty" : "available",
            runId, profileId, observed, truncated, incomplete, calls, more ? calls[^1].Sequence : null, Aggregate: aggregate, Finalized: finalized);
    }

    public async Task<int> ExpireModelProfilesAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.GetDbConnection().BeginTransactionAsync(ct).ConfigureAwait(false);
        var connection = db.Database.GetDbConnection();
        // At most batchSize profiles, each containing at most 10,000 detail rows. Summary rows remain.
        await using var delete = ProfileCommand(connection, transaction, """
            DELETE FROM ModelCalls WHERE RunId IN (
                SELECT RunId FROM ModelProfiles WHERE Expired=0 AND ExpiresTicks <= $now ORDER BY ExpiresTicks,RunId LIMIT $batch)
            """, ("$now", now.UtcTicks), ("$batch", batchSize));
        await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var mark = ProfileCommand(connection, transaction, """
            UPDATE ModelProfiles SET Expired=1 WHERE RunId IN (
                SELECT RunId FROM ModelProfiles WHERE Expired=0 AND ExpiresTicks <= $now ORDER BY ExpiresTicks,RunId LIMIT $batch)
            """, ("$now", now.UtcTicks), ("$batch", batchSize));
        var changed = await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return changed;
    }
}
