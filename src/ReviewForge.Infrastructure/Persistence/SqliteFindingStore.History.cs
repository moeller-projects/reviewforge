using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Persistence;

public sealed partial class SqliteFindingStore
{
    public async Task<RunHistoryPage> GetRunHistoryAsync(int offset, int count, PrKey? pr, bool attentionOnly, CancellationToken ct,
        IReadOnlyCollection<Guid>? reconcileRunIds = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
        // Only scalar JSON properties are inspected. No FindingJson or observation array crosses
        // the database boundary. The latest decision uses the same identity rules as ResolveFindings.
        cmd.CommandText = """
            WITH filtered AS (
                SELECT r.* FROM Runs r WHERE ($org IS NULL OR
                    (r.Org=$org AND r.Project=$project AND r.RepositoryId=$repo AND r.PrId=$pr))
            ), scoped AS (
                SELECT * FROM filtered WHERE $attention OR Id IN
                    (SELECT Id FROM filtered ORDER BY StartedAtUtcTicks DESC,Id DESC LIMIT $count OFFSET $offset)
            ), decision_candidates AS (
                SELECT r.Id RunId, lower(replace(json_extract(o.value,'$.id'),'-','')) FindingId,
                    (SELECT f.Id FROM Findings f WHERE f.RunId=r.Id
                        AND f.DedupeKey=json_extract(o.value,'$.dedupeKey')
                        AND substr(f.DedupeKey,1,7)!='thread-' AND f.FindingJson IS NOT NULL ORDER BY f.Id LIMIT 1) RowId,
                    json_extract(o.value,'$.validation')=1 AND json_extract(o.value,'$.verification')!=2 Eligible
                FROM scoped r, json_each(r.FindingObservationsJson) o
                UNION ALL
                SELECT r.Id, f.DedupeKey, f.Id, 1 FROM scoped r JOIN Findings f ON f.RunId=r.Id
                WHERE r.FindingObservationsJson IS NULL AND f.FindingJson IS NOT NULL AND substr(f.DedupeKey,1,7)!='thread-'
            ), decision_counts AS (
                SELECT c.RunId,
                    SUM(CASE WHEN d.State IS NULL OR d.State='Pending' THEN 1 ELSE 0 END) Pending,
                    SUM(CASE WHEN d.State='Approved' AND c.Eligible AND f.FindingJson IS NOT NULL
                        AND f.Published=0 AND f.ThreadId IS NULL THEN 1 ELSE 0 END) Approved
                FROM decision_candidates c LEFT JOIN Findings f ON f.Id=c.RowId
                LEFT JOIN FindingDecisions d ON d.RunId=c.RunId AND d.FindingId=c.FindingId
                    AND d.Version=(SELECT MAX(v.Version) FROM FindingDecisions v WHERE v.RunId=c.RunId AND v.FindingId=c.FindingId)
                GROUP BY c.RunId
            ), selected AS (
                SELECT r.*, COALESCE(d.Pending,0) Pending, COALESCE(d.Approved,0) Approved
                FROM scoped r LEFT JOIN decision_counts d ON d.RunId=r.Id
                WHERE NOT $attention OR r.CompletedAt IS NULL OR r.Success=0 OR
                    (r.Pipeline='ReviewDraft' AND (COALESCE(d.Pending,0)>0 OR COALESCE(d.Approved,0)>0))
                    OR r.Id IN (SELECT value FROM json_each($reconcile))
                ORDER BY r.StartedAtUtcTicks DESC,r.Id DESC LIMIT $count OFFSET CASE WHEN $attention THEN $offset ELSE 0 END
            )
            SELECT r.Id,r.Org,r.Project,r.RepositoryId,r.PrId,r.HeadSha,r.Pipeline,r.StartedAt,r.CompletedAt,r.Success,
                r.FindingObservationsJson IS NOT NULL,
                (SELECT COUNT(*) FROM json_each(r.FindingObservationsJson)),
                (SELECT COUNT(*) FROM json_each(r.FindingObservationsJson) o WHERE NOT json_extract(o.value,'$.previouslyKnown')
                    AND json_extract(o.value,'$.validation')=1 AND json_extract(o.value,'$.verification')!=2),
                (SELECT COUNT(*) FROM json_each(r.FindingObservationsJson) o WHERE json_extract(o.value,'$.validation')=2 OR json_extract(o.value,'$.verification')=2),
                (SELECT COUNT(*) FROM json_each(r.FindingObservationsJson) o WHERE json_extract(o.value,'$.previouslyKnown')),
                (SELECT COUNT(*) FROM (SELECT json_extract(o.value,'$.dedupeKey') FROM json_each(r.FindingObservationsJson) o
                    WHERE json_extract(o.value,'$.newlyPosted') GROUP BY json_extract(o.value,'$.dedupeKey'))),
                r.Pending,r.Approved
            FROM selected r ORDER BY r.StartedAtUtcTicks DESC,r.Id DESC
            """;
        cmd.Parameters.AddWithValue("$org", (object?)pr?.Org ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)pr?.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo", (object?)pr?.RepositoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr", (object?)pr?.PrId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reconcile", System.Text.Json.JsonSerializer.Serialize(
            (reconcileRunIds ?? []).Select(id => id.ToString().ToUpperInvariant())));
        cmd.Parameters.AddWithValue("$attention", attentionOnly);
        cmd.Parameters.AddWithValue("$offset", offset);
        cmd.Parameters.AddWithValue("$count", count + 1);
        var rows = new List<RunHistoryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new(reader.GetGuid(0), new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)),
                string.IsNullOrWhiteSpace(reader.GetString(5)) ? null : reader.GetString(5), reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetBoolean(9),
                reader.GetBoolean(10) ? new(reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15)) : null,
                reader.GetInt32(16), reader.GetInt32(17)));
        return new(rows.Take(count).ToArray(), rows.Count > count);
    }
}
