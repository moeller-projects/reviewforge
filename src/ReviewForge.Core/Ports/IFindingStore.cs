using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>FP-keyed finding store: run history and posted findings per PR.</summary>
public interface IFindingStore
{
    Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct);

    Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct);

    /// <summary>
    /// Saves a run. Upsert semantics: unknown run id → insert (in-flight shell when
    /// CompletedAt/Success say so); known run id → finalize (update head/completion/success,
    /// merge any missing finding rows, keep backfilled thread ids).
    /// </summary>
    Task SaveRunAsync(ReviewRun run, CancellationToken ct);

    /// <summary>Backfill the posted thread id for a finding of the run.</summary>
    Task SetThreadIdAsync(Guid runId, string dedupeKey, int threadId, CancellationToken ct);

    /// <summary>Newest runs for the PR regardless of outcome (failure backoff, diagnostics).</summary>
    Task<IReadOnlyList<ReviewRun>> GetRecentRunsAsync(PrKey pr, int count, CancellationToken ct);
}