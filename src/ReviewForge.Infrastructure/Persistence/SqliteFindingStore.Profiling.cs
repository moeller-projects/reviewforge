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
        CREATE TABLE IF NOT EXISTS ExecutionActivities (
            RunId TEXT NOT NULL, Sequence INTEGER NOT NULL, ActivityId TEXT NOT NULL, MetadataJson TEXT NOT NULL,
            PRIMARY KEY (RunId, Sequence), UNIQUE (ActivityId));
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
        if (profile.Calls.Count + (profile.Activities?.Count ?? 0) > 10_000) throw new ArgumentOutOfRangeException(nameof(profile));
        if (await BeginModelProfileAsync(profile, ct).ConfigureAwait(false) != profile.ProfileId)
            throw new ArgumentException("Model profile identity mismatch.", nameof(profile));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.GetDbConnection().BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = ProfileCommand(db.Database.GetDbConnection(), transaction, """
            UPDATE ModelProfiles SET ObservedCalls=$observed, Truncated=$truncated, Incomplete=$incomplete,
                Finalized=1, CompletedTicks=$completed, ExpiresTicks=$expires, SummaryJson=$summary, SchemaVersion=$schema
            WHERE RunId=$run AND ProfileId=$profile AND Finalized=0
            """, ("$observed", profile.ObservedCalls), ("$truncated", profile.Truncated),
            ("$incomplete", incomplete || profile.Incomplete || profile.Aggregate?.InterruptedCalls > 0 || profile.Calls.Any(c => c.Outcome == "interrupted")
                || profile.Aggregate?.Activities?.Values.Any(a => a.Interrupted > 0) == true || profile.Activities?.Any(a => a.Outcome == "interrupted") == true),
            ("$schema", profile.SchemaVersion),
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
            foreach (var activity in profile.Activities ?? [])
            {
                if (activity.RunId != profile.RunId || activity.ProfileId != profile.ProfileId)
                    throw new ArgumentException("Profile correlation mismatch.", nameof(profile));
                await using var insert = ProfileCommand(db.Database.GetDbConnection(), transaction, """
                    INSERT INTO ExecutionActivities (RunId,Sequence,ActivityId,MetadataJson) VALUES ($run,$sequence,$id,$json)
                    """, ("$run", profile.RunId.ToString("D")), ("$sequence", activity.Sequence),
                    ("$id", activity.Id.ToString("D")), ("$json", JsonSerializer.Serialize(activity)));
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
            SELECT ProfileId,ObservedCalls,Truncated,Incomplete,Finalized,ExpiresTicks,Expired,SummaryJson,SchemaVersion FROM ModelProfiles WHERE RunId=$run
            """, ("$run", runId.ToString("D")));
        Guid profileId;
        long observed;
        bool truncated, incomplete, expired, finalized;
        ModelProfileAggregate? aggregate;
        int schema;
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
            schema = reader.GetInt32(8);
        }
        if (expired) return new("expired", runId, profileId, observed, truncated, incomplete, [], null, SchemaVersion: schema, Aggregate: aggregate, Finalized: finalized, Activities: []);
        var calls = new List<ModelCallRecord>();
        var activities = new List<ExecutionActivityRecord>();
        var rows = new List<(long Sequence, string Kind, string Json)>();
        await using var details = ProfileCommand(connection, transaction, """
            SELECT Sequence,'model' AS Kind,MetadataJson FROM ModelCalls WHERE RunId=$run AND Sequence>$after
            UNION ALL
            SELECT Sequence,'activity' AS Kind,MetadataJson FROM ExecutionActivities WHERE RunId=$run AND Sequence>$after
            ORDER BY Sequence LIMIT $count
            """, ("$run", runId.ToString("D")), ("$after", afterSequence), ("$count", count + 1));
        await using (var reader = await details.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        var more = rows.Count > count;
        if (more) rows.RemoveAt(rows.Count - 1);
        foreach (var row in rows)
            if (row.Kind == "model") calls.Add(JsonSerializer.Deserialize<ModelCallRecord>(row.Json)!);
            else activities.Add(JsonSerializer.Deserialize<ExecutionActivityRecord>(row.Json)!);
        return new(incomplete ? "incomplete" : truncated ? "truncated" : observed == 0 && aggregate?.Activities?.Count is not > 0 ? "empty" : "available",
            runId, profileId, observed, truncated, incomplete, calls, more ? rows[^1].Sequence : null,
            SchemaVersion: schema, Aggregate: aggregate, Finalized: finalized, Activities: activities);
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
                SELECT RunId FROM ModelProfiles WHERE Expired=0 AND ExpiresTicks <= $now ORDER BY ExpiresTicks,RunId LIMIT $batch);
            DELETE FROM ExecutionActivities WHERE RunId IN (
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
