using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Testing;

public partial class FakeFindingStore
{
    public Exception? ThrowOnHistory { get; set; }

    public Task<RunHistoryPage> GetRunHistoryAsync(int offset, int count, PrKey? pr, bool attentionOnly, CancellationToken ct,
        IReadOnlyCollection<Guid>? reconcileRunIds = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        ct.ThrowIfCancellationRequested();
        if (ThrowOnHistory is { } error) return Task.FromException<RunHistoryPage>(error);
        lock (Runs)
        {
            var rows = Runs.Where(r => pr is null || r.Pr == pr).OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id.ToString("D"), StringComparer.Ordinal).Select(ProjectHistory)
                .Where(r => !attentionOnly || r.NeedsAttention || reconcileRunIds?.Contains(r.RunId) == true)
                .Skip(offset).Take(count + 1).ToArray();
            return Task.FromResult(new RunHistoryPage(rows.Take(count).ToArray(), rows.Length > count));
        }
    }

    private RunHistoryItem ProjectHistory(ReviewRun run)
    {
        var rows = run.Findings.Where(f => f.FindingJson is not null && !f.DedupeKey.StartsWith("thread-", StringComparison.Ordinal)).ToArray();
        var candidates = run.FindingObservations is null
            ? rows.Select(f => (Id: f.DedupeKey, Row: (StoredFinding?)f, Eligible: true))
            : run.FindingObservations.Select(o => (Id: o.Id.ToString("N"),
                Row: o.DedupeKey is null ? null : rows.FirstOrDefault(f => f.DedupeKey == o.DedupeKey),
                Eligible: o.Validation == FindingValidationStatus.Accepted && o.Verification != FindingVerificationStatus.Rejected));
        var pending = 0;
        var approved = 0;
        foreach (var c in candidates)
        {
            var state = _FindingDecisions.Where(d => d.RunId == run.Id && d.FindingId == c.Id).MaxBy(d => d.Version)?.State;
            if (state is null or FindingDecisionState.Pending) pending++;
            if (state == FindingDecisionState.Approved && c.Eligible && c.Row is { Published: false, ThreadId: null }) approved++;
        }
        return new(run.Id, run.Pr, string.IsNullOrWhiteSpace(run.HeadSha) ? null : run.HeadSha, run.Pipeline,
            run.StartedAt, run.CompletedAt, run.Success,
            run.FindingObservations is null ? null : FindingObservationCounts.From(run.FindingObservations), pending, approved);
    }
}
