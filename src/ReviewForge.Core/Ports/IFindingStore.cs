using ReviewForge.Core.Domain;

namespace ReviewForge.Core.Ports;

/// <summary>Durable record of one fix whose commit is intended for — or was pushed to — the
/// PR source branch (CommitOnHead mode). The commit stage persists the rows as a push INTENT
/// (<see cref="Pushed"/> false) BEFORE the external push, confirms them atomically after a
/// successful push, and abandons them on rejection — so a crash anywhere in that sequence
/// leaves either no visible rows or rows a later run can reconcile BEFORE any reply is
/// attempted. Only confirmed rows (<see cref="Pushed"/> true) are eligible for reply
/// reconciliation. <see cref="Id"/> is store-assigned (0 on save); <see cref="RunId"/>,
/// <see cref="Pushed"/>, <see cref="ReplyPosted"/> and <see cref="CreatedAt"/> are likewise
/// store-owned on save.</summary>
public sealed record PushedFix(
    int Id,
    Guid RunId,
    string DedupeKey,          // finding key, or "thread-{ThreadId}" for commanded fixes
    string CommitSha,
    string CommitSubject,
    int? ThreadId,             // resolved live thread when known (commanded fixes)
    bool Pushed,               // false = push intent only; true = push confirmed on the remote
    bool ReplyPosted,
    DateTimeOffset CreatedAt);
 

/// <summary>FP-keyed finding store: run history and posted findings per PR.</summary>
public interface IFindingStore
{
    Task<PriorRun?> GetLastCompletedRunAsync(PrKey pr, CancellationToken ct);

    Task<IReadOnlyList<string>> GetKnownDedupeKeysAsync(PrKey pr, CancellationToken ct);
    /// <summary>Returns commanded-fix thread ids already recorded as audit rows for this PR.</summary>
    Task<IReadOnlySet<long>> GetCommandedFixThreadIdsAsync(PrKey pr, CancellationToken ct);

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

    /// <summary>The run row by id regardless of outcome, or null when unknown. Backs status
    /// read-through after a tracker miss (host restart, retention expiry).</summary>
    Task<ReviewRun?> GetRunAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// In-flight shells (CompletedAt == null) older than <paramref name="olderThan"/>, across
    /// all PRs — startup reaper input (P1-12). Findings are not loaded.
    /// </summary>
    Task<IReadOnlyList<ReviewRun>> GetStaleShellsAsync(DateTimeOffset olderThan, CancellationToken ct);

    /// <summary>
    /// Deletes runs older than <paramref name="olderThan"/> (P2-26), keeping at least
    /// <paramref name="minRunsPerPr"/> runs per PR regardless of age and never the latest
    /// completed run per PR (dedupe continuity). Finding rows of pruned runs are deleted
    /// with them. Returns the number of runs pruned.
    /// </summary>
    Task<int> PruneAsync(DateTimeOffset olderThan, int minRunsPerPr, CancellationToken ct);

    /// <summary>Persists the pushed-fix records of a run as push INTENT (Pushed = false).
    /// Called by the commit stage BEFORE the external push so a crash leaves a durable record;
    /// intent rows are invisible to reply reconciliation until confirmed.</summary>
    Task SavePushedFixesAsync(PrKey pr, Guid runId, IReadOnlyList<PushedFix> fixes, CancellationToken ct);

    /// <summary>Atomically confirms the run's push-intent rows after a successful push
    /// (Pushed = true), making them eligible for reply reconciliation.</summary>
    Task ConfirmPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct);

    /// <summary>Deletes the run's unconfirmed push-intent rows after a rejected push so a
    /// commit that never reached the remote is never reconciled as pushed.</summary>
    Task AbandonPushedFixesAsync(PrKey pr, Guid runId, CancellationToken ct);

    /// <summary>Confirmed (Pushed) fixes of this PR whose "Fixed in {sha}" reply has not been
    /// posted yet (crash orphans of prior runs plus the current run's fresh rows).</summary>
    Task<IReadOnlyList<PushedFix>> GetUnrepliedPushedFixesAsync(PrKey pr, CancellationToken ct);

    /// <summary>Marks one pushed-fix row as replied (exactly-once convergence with the
    /// reply-text dedupe check).</summary>
    Task MarkPushedFixRepliedAsync(int pushedFixId, CancellationToken ct);

    /// <summary>Connectivity probe for health checks; must not depend on any PR-scoped data.</summary>
    Task<ReviewRun?> GetLastCompletedResolveRunAsync(PrKey pr, CancellationToken ct);
    Task<IReadOnlyList<ResolveAction>> GetResolveActionsAsync(
        PrKey pr, IReadOnlyCollection<int> threadIds, CancellationToken ct);
    Task SaveResolveActionsAsync(
        PrKey pr, Guid runId, IReadOnlyList<ResolveAction> actions, CancellationToken ct);
    Task MarkResolveActionRepliedAsync(int id, CancellationToken ct);
    Task PingAsync(CancellationToken ct);
}