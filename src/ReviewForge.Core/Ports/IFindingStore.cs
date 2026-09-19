using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>FP-keyed finding store: run history and posted findings per PR.</summary>
public interface IFindingStore
{
    Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct);

    Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct);

    Task SaveRunAsync(ReviewRun run, CancellationToken ct);

    /// <summary>Backfill the posted thread id for a finding of the run.</summary>
    Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct);
}