using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;

namespace ReviewForge.Infrastructure.Persistence;

public sealed partial class SqliteFindingStore
{
    public Task<IReadOnlyList<FindingDecision>> GetFindingDecisionsAsync(Guid runId, CancellationToken ct)
        => ReadDecisionsAsync(runId, null, 0, ct);

    public Task<IReadOnlyList<FindingDecision>> GetFindingDecisionHistoryAsync(
        Guid runId, string findingId, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        return ReadDecisionsAsync(runId, findingId, count, ct);
    }

    private async Task<IReadOnlyList<FindingDecision>> ReadDecisionsAsync(Guid runId, string? findingId, int count, CancellationToken ct)
    {
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = ((SqliteConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = findingId is null ? """
            SELECT d.RunId, d.FindingId, d.HeadSha, d.State, d.Reason, d.Actor, d.DecidedAt, d.Version
            FROM FindingDecisions d WHERE d.RunId=$run AND d.Version=(
                SELECT MAX(v.Version) FROM FindingDecisions v WHERE v.RunId=d.RunId AND v.FindingId=d.FindingId)
            ORDER BY d.FindingId
            """ : """
            SELECT RunId, FindingId, HeadSha, State, Reason, Actor, DecidedAt, Version
            FROM FindingDecisions WHERE RunId=$run AND FindingId=$finding ORDER BY Version DESC LIMIT $count
            """;
        command.Parameters.AddWithValue("$run", runId);
        if (findingId is not null)
        {
            command.Parameters.AddWithValue("$finding", findingId);
            command.Parameters.AddWithValue("$count", count);
        }
        var result = new List<FindingDecision>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(FindingDecision.Record(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                Enum.Parse<FindingDecisionState>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7)));
        return result;
    }

    public async Task<bool> TryAppendFindingDecisionAsync(FindingDecision decision, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || decision.Version != expectedVersion + 1)
            throw new ArgumentException("Decision version must follow expectedVersion.", nameof(expectedVersion));
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        // Serialize membership/revision check, latest-version check and append with pruning/writers.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", decision.RunId);
        command.CommandText = "SELECT HeadSha FROM Runs WHERE Id=$run";
        var head = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (!string.Equals(head, decision.HeadSha, StringComparison.Ordinal))
            throw new ArgumentException("Decision run or revision does not match a stored run.", nameof(decision));
        command.Parameters.AddWithValue("$finding", decision.FindingId);
        command.CommandText = "SELECT COALESCE(MAX(Version),0) FROM FindingDecisions WHERE RunId=$run AND FindingId=$finding";
        var current = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (current != expectedVersion) return false;
        command.CommandText = """
            INSERT INTO FindingDecisions (RunId,FindingId,HeadSha,State,Reason,Actor,DecidedAt,Version)
            VALUES ($run,$finding,$head,$state,$reason,$actor,$at,$version)
            """;
        command.Parameters.AddWithValue("$head", decision.HeadSha);
        command.Parameters.AddWithValue("$state", decision.State.ToString());
        command.Parameters.AddWithValue("$reason", (object?)decision.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", decision.Actor);
        command.Parameters.AddWithValue("$at", decision.DecidedAt);
        command.Parameters.AddWithValue("$version", decision.Version);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
