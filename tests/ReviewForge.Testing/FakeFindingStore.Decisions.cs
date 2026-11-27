using ReviewForge.Core.Domain;

namespace ReviewForge.Testing;

public partial class FakeFindingStore
{
    private readonly List<FindingDecision> _FindingDecisions = [];

    public Task<IReadOnlyList<FindingDecision>> GetFindingDecisionsAsync(Guid runId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Runs)
            return Task.FromResult<IReadOnlyList<FindingDecision>>(_FindingDecisions.Where(d => d.RunId == runId)
                .GroupBy(d => d.FindingId, StringComparer.Ordinal).Select(g => g.MaxBy(d => d.Version)!)
                .OrderBy(d => d.FindingId, StringComparer.Ordinal).ToArray());
    }

    public Task<IReadOnlyList<FindingDecision>> GetFindingDecisionHistoryAsync(Guid runId, string findingId, int count, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(findingId);
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        lock (Runs)
            return Task.FromResult<IReadOnlyList<FindingDecision>>(_FindingDecisions
                .Where(d => d.RunId == runId && d.FindingId == findingId).OrderByDescending(d => d.Version).Take(count).ToArray());
    }

    public Func<FindingDecision, CancellationToken, Task>? BeforeFindingDecisionAppend { get; set; }

    public async Task<bool> TryAppendFindingDecisionAsync(FindingDecision decision, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || decision.Version != expectedVersion + 1)
            throw new ArgumentException("Decision version must follow expectedVersion.", nameof(expectedVersion));
        if (BeforeFindingDecisionAppend is not null) await BeforeFindingDecisionAppend(decision, ct);
        ct.ThrowIfCancellationRequested();
        lock (Runs)
        {
            var run = Runs.FirstOrDefault(r => r.Id == decision.RunId);
            if (run is null || !string.Equals(run.HeadSha, decision.HeadSha, StringComparison.Ordinal))
                throw new ArgumentException("Decision run or revision does not match a stored run.", nameof(decision));
            var current = _FindingDecisions.Where(d => d.RunId == decision.RunId && d.FindingId == decision.FindingId)
                .Select(d => d.Version).DefaultIfEmpty(0).Max();
            if (current != expectedVersion) return false;
            _FindingDecisions.Add(decision);
            return true;
        }
    }
}
