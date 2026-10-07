using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed durable review queue on the store's database file. Enqueue, claim, and ack
/// are single immediate transactions; WAL plus busy_timeout keep them contention-free with
/// the finding store. Claimed rows expire after the claim TTL, so a crashed or restarted
/// worker's run becomes claimable again — the same recovery semantic as the in-memory
/// InFlightClaims. Poll-based dequeue: each <see cref="ReadAllAsync"/> enumerator loops
/// claim-then-wait, so competing consumers never double-claim a row.
/// </summary>
public sealed class SqliteReviewQueue : IReviewQueue
{
    private readonly string _ConnectionString;
    private readonly int _Capacity;
    private readonly TimeProvider _Clock;
    private readonly TimeSpan _ClaimTtl;
    private readonly TimeSpan _PollInterval;
    private readonly string _Pragmas;
    private readonly string _ClaimWorker = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public SqliteReviewQueue(
        string connectionString,
        int capacity = 100,
        TimeProvider? clock = null,
        TimeSpan? claimTtl = null,
        TimeSpan? pollInterval = null,
        StoreJournalMode journalMode = StoreJournalMode.Wal)
    {
        _ConnectionString = connectionString;
        _Capacity = capacity;
        _Clock = clock ?? TimeProvider.System;
        _ClaimTtl = claimTtl ?? TimeSpan.FromMinutes(10);
        _PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        // Mirror SqliteConnectionPragmasInterceptor: WAL pairs with synchronous=NORMAL;
        // Delete keeps FULL durability for filesystems without POSIX advisory locks.
        _Pragmas = journalMode == StoreJournalMode.Wal
            ? "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;"
            : "PRAGMA busy_timeout=5000; PRAGMA journal_mode=DELETE;";
        EnsureSchema();
    }

    public int Capacity => _Capacity;

    public int ApproximateDepth
    {
        get
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM QueuedRuns WHERE ClaimedBy IS NULL";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public EnqueueResult TryEnqueue(ReviewRequest request)
    {
        using var connection = Open();
        // BEGIN IMMEDIATE serializes the capacity check + insert against concurrent enqueues.
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM QueuedRuns WHERE ClaimedBy IS NULL";
            var unclaimed = Convert.ToInt32(count.ExecuteScalar());
            if (unclaimed >= _Capacity)
            {
                QueueTelemetry.QueueRejected.Add(1);
                return new EnqueueResult(false, unclaimed);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO QueuedRuns (RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState, Trigger, Kind) " +
            "VALUES ($runId, $org, $project, $repo, $prId, $enqueuedAt, $headSha, $traceParent, $traceState, $trigger, $kind);";
        insert.Parameters.AddWithValue("$runId", request.RunId.ToString());
        insert.Parameters.AddWithValue("$org", request.Pr.Org);
        insert.Parameters.AddWithValue("$project", request.Pr.Project);
        insert.Parameters.AddWithValue("$repo", request.Pr.RepositoryId);
        insert.Parameters.AddWithValue("$prId", request.Pr.PrId);
        insert.Parameters.AddWithValue("$enqueuedAt", Stamp(request.EnqueuedAt));
        insert.Parameters.AddWithValue("$headSha", (object?) request.HeadSha ?? DBNull.Value);
        insert.Parameters.AddWithValue("$traceParent", (object?) TraceParentOf(request.EnqueueContext) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$traceState", (object?) request.EnqueueContext?.TraceState ?? DBNull.Value);
        insert.Parameters.AddWithValue("$trigger", request.Trigger.ToString());
        insert.Parameters.AddWithValue("$kind", request.Kind.ToString());
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new EnqueueResult(true, ApproximateDepth);
    }

    public async IAsyncEnumerable<ReviewRequest> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var claimed = await TryClaimAsync(ct).ConfigureAwait(false);
            if (claimed is not null)
            {
                yield return claimed;
                continue;
            }

            await Task.Delay(_PollInterval, _Clock, ct).ConfigureAwait(false);
        }
    }

    public void Acknowledge(Guid runId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM QueuedRuns WHERE RunId = $runId";
        cmd.Parameters.AddWithValue("$runId", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    public bool RenewClaim(Guid runId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "UPDATE QueuedRuns SET ClaimedAt = $now WHERE RunId = $runId AND ClaimedBy = $worker;";
        cmd.Parameters.AddWithValue("$now", Stamp(_Clock.GetUtcNow()));
        cmd.Parameters.AddWithValue("$worker", _ClaimWorker);
        cmd.Parameters.AddWithValue("$runId", runId.ToString());
        return cmd.ExecuteNonQuery() > 0;
    }

    public ReviewRequest? TryGetQueued(Guid runId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState, Trigger, Kind " +
            "FROM QueuedRuns WHERE RunId = $runId AND ClaimedBy IS NULL";
        cmd.Parameters.AddWithValue("$runId", runId.ToString());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRequest(reader) : null;
    }

    private async Task<ReviewRequest?> TryClaimAsync(CancellationToken ct)
    {
        var now = _Clock.GetUtcNow();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        ReviewRequest? request;
        bool reclaimed;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState, Trigger, Kind, " +
                "ClaimedBy IS NOT NULL FROM QueuedRuns " +
                "WHERE ClaimedBy IS NULL OR ClaimedAt < $reclaimBefore ORDER BY rowid LIMIT 1;";
            select.Parameters.AddWithValue("$reclaimBefore", Stamp(now - _ClaimTtl));
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            request = ReadRequest(reader);
            reclaimed = reader.GetBoolean(11);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE QueuedRuns SET ClaimedBy = $worker, ClaimedAt = $now WHERE RunId = $runId;";
            update.Parameters.AddWithValue("$worker", _ClaimWorker);
            update.Parameters.AddWithValue("$now", Stamp(now));
            update.Parameters.AddWithValue("$runId", request.RunId.ToString());
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        if (reclaimed)
        {
            QueueTelemetry.QueueReclaimed.Add(1);
        }

        return request;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        // CREATE IF NOT EXISTS is safe under concurrent startup; serialize the guarded
        // PRAGMA/ALTER migration so two hosts cannot both observe a missing column.
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS QueuedRuns (" +
            "RunId TEXT PRIMARY KEY, " +
            "Org TEXT NOT NULL, Project TEXT NOT NULL, RepositoryId TEXT NOT NULL, PrId INTEGER NOT NULL, " +
            "EnqueuedAt TEXT NOT NULL, HeadSha TEXT NULL, " +
            "TraceParent TEXT NULL, TraceState TEXT NULL, Trigger TEXT NULL, Kind TEXT NULL, " +
            "ClaimedBy TEXT NULL, ClaimedAt TEXT NULL);";
        cmd.ExecuteNonQuery();

        // Null reads as Manual — pre-migration rows cannot reference bot-authored heads
        // (no code existed to create one), so the worst case is one redundant review.
        cmd.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('QueuedRuns') WHERE name = 'Trigger'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = "ALTER TABLE QueuedRuns ADD COLUMN Trigger TEXT NULL";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('QueuedRuns') WHERE name = 'Kind'";
        if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
        {
            cmd.CommandText = "ALTER TABLE QueuedRuns ADD COLUMN Kind TEXT NULL";
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_ConnectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = _Pragmas;
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            // Never leak the native handle when open/pragma fails (transient lock or
            // journal errors during startup/polling).
            connection.Dispose();
            throw;
        }
    }

    // UTC "O" stamps: lexicographic comparison in SQL equals chronological comparison.
    private static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string stamp)
        => DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string? TraceParentOf(ActivityContext? context)
    {
        if (context is not { } ctx || ctx.TraceId == default)
        {
            return null;
        }

        var flags = ctx.TraceFlags.HasFlag(ActivityTraceFlags.Recorded) ? "01" : "00";
        return $"00-{ctx.TraceId}-{ctx.SpanId}-{flags}";
    }

    private static ReviewRequest ReadRequest(DbDataReader reader)
    {
        ActivityContext? context = null;
        var traceParent = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (traceParent is not null
            && ActivityContext.TryParse(traceParent, reader.IsDBNull(8) ? null : reader.GetString(8), isRemote: true, out var parsed))
        {
            context = parsed;
        }

        var triggerText = reader.IsDBNull(9) ? null : reader.GetString(9);
        var trigger = triggerText is not null && Enum.TryParse<EnqueueTrigger>(triggerText, out var parsedTrigger)
                                              && Enum.IsDefined(parsedTrigger)
            ? parsedTrigger
            : EnqueueTrigger.Manual;
        var kindText = reader.IsDBNull(10) ? null : reader.GetString(10);
        var kind = kindText is not null && Enum.TryParse<RunKind>(kindText, out var parsedKind)
                                        && Enum.IsDefined(parsedKind)
            ? parsedKind
            : RunKind.Review;

        return new ReviewRequest(
            Guid.Parse(reader.GetString(0)),
            new PrKey(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)),
            ParseStamp(reader.GetString(5)),
            context,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            trigger,
            kind);
    }
}