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
    private readonly string _ClaimWorker = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    public SqliteReviewQueue(
        string connectionString,
        int capacity = 100,
        TimeProvider? clock = null,
        TimeSpan? claimTtl = null,
        TimeSpan? pollInterval = null)
    {
        _ConnectionString = connectionString;
        _Capacity = capacity;
        _Clock = clock ?? TimeProvider.System;
        _ClaimTtl = claimTtl ?? TimeSpan.FromMinutes(10);
        _PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
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
                ReviewForgeTelemetry.QueueRejected.Add(1);
                return new EnqueueResult(false, unclaimed);
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO QueuedRuns (RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState) " +
            "VALUES ($runId, $org, $project, $repo, $prId, $enqueuedAt, $headSha, $traceParent, $traceState);";
        insert.Parameters.AddWithValue("$runId", request.RunId.ToString());
        insert.Parameters.AddWithValue("$org", request.Pr.Org);
        insert.Parameters.AddWithValue("$project", request.Pr.Project);
        insert.Parameters.AddWithValue("$repo", request.Pr.RepositoryId);
        insert.Parameters.AddWithValue("$prId", request.Pr.PrId);
        insert.Parameters.AddWithValue("$enqueuedAt", Stamp(request.EnqueuedAt));
        insert.Parameters.AddWithValue("$headSha", (object?)request.HeadSha ?? DBNull.Value);
        insert.Parameters.AddWithValue("$traceParent", (object?)TraceParentOf(request.EnqueueContext) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$traceState", (object?)request.EnqueueContext?.TraceState ?? DBNull.Value);
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

    public ReviewRequest? TryGetQueued(Guid runId)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState " +
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
        var reclaimed = false;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT RunId, Org, Project, RepositoryId, PrId, EnqueuedAt, HeadSha, TraceParent, TraceState, " +
                "ClaimedBy IS NOT NULL FROM QueuedRuns " +
                "WHERE ClaimedBy IS NULL OR ClaimedAt < $reclaimBefore ORDER BY rowid LIMIT 1;";
            select.Parameters.AddWithValue("$reclaimBefore", Stamp(now - _ClaimTtl));
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            request = ReadRequest(reader);
            reclaimed = reader.GetBoolean(9);
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
            ReviewForgeTelemetry.QueueReclaimed.Add(1);
        }

        return request;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS QueuedRuns (" +
            "RunId TEXT PRIMARY KEY, " +
            "Org TEXT NOT NULL, Project TEXT NOT NULL, RepositoryId TEXT NOT NULL, PrId INTEGER NOT NULL, " +
            "EnqueuedAt TEXT NOT NULL, HeadSha TEXT NULL, " +
            "TraceParent TEXT NULL, TraceState TEXT NULL, " +
            "ClaimedBy TEXT NULL, ClaimedAt TEXT NULL);";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // UTC "O" stamps: lexicographic comparison in SQL equals chronological comparison.
    private static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string stamp)
        => new(DateTime.ParseExact(stamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), TimeSpan.Zero);

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

        return new ReviewRequest(
            Guid.Parse(reader.GetString(0)),
            new PrKey(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)),
            ParseStamp(reader.GetString(5)),
            context,
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
