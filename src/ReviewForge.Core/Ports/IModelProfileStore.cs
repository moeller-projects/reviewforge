using ReviewForge.Core.Profiling;

namespace ReviewForge.Core.Ports;

public sealed record ModelProfilePage(string State, Guid RunId, Guid? ProfileId, long ObservedCalls,
    bool Truncated, bool Incomplete, IReadOnlyList<ModelCallRecord> Calls, long? NextSequence,
    int SchemaVersion = 1, string RetryCoverage = "sdk-internal-unavailable", ModelProfileAggregate? Aggregate = null,
    bool Finalized = false);

/// <summary>IO boundary for bounded metadata only. Detailed rows are separate from permanent summaries.</summary>
public interface IModelProfileStore
{
    Task<Guid> BeginModelProfileAsync(ModelProfileSnapshot profile, CancellationToken ct);
    Task FinalizeModelProfileAsync(ModelProfileSnapshot profile, DateTimeOffset completedAt,
        DateTimeOffset expiresAt, bool incomplete, CancellationToken ct);
    Task<ModelProfilePage> ReadModelProfileAsync(Guid runId, long afterSequence, int count, DateTimeOffset now, CancellationToken ct);
    Task<int> ExpireModelProfilesAsync(DateTimeOffset now, int batchSize, CancellationToken ct);
}
