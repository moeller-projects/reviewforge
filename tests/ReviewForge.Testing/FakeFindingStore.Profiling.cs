using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;

namespace ReviewForge.Testing;

public partial class FakeFindingStore : IModelProfileStore
{
    private readonly Dictionary<Guid, (ModelProfileSnapshot Snapshot, DateTimeOffset? Expires, bool Incomplete)> _Profiles = [];
    public bool ThrowOnModelProfileWrite { get; set; }
    public bool ThrowOnModelProfileFinalize { get; set; }
    public bool ThrowOnModelProfileRead { get; set; }
    public Action<CancellationToken>? BeforeModelProfileRead { get; set; }
    public bool ThrowOnModelProfileCleanup { get; set; }
    public CancellationToken LastModelProfileWriteToken { get; private set; }
    public Task<Guid> BeginModelProfileAsync(ModelProfileSnapshot profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastModelProfileWriteToken = ct;
        if (ThrowOnModelProfileWrite) throw new InvalidOperationException("profile write failed");
        lock (_Profiles)
        {
            _Profiles.TryAdd(profile.RunId, (profile with { Calls = [], Activities = [], ObservedCalls = 0, Aggregate = null }, null, true));
            return Task.FromResult(_Profiles[profile.RunId].Snapshot.ProfileId);
        }
    }
    public Task FinalizeModelProfileAsync(ModelProfileSnapshot profile, DateTimeOffset completedAt, DateTimeOffset expiresAt,
        bool incomplete, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LastModelProfileWriteToken = ct;
        if (ThrowOnModelProfileWrite) throw new InvalidOperationException("profile write failed");
        if (ThrowOnModelProfileFinalize) throw new InvalidOperationException("profile finalization failed");
        lock (_Profiles)
        {
            if (!_Profiles.TryGetValue(profile.RunId, out var existing) || existing.Expires is null)
                _Profiles[profile.RunId] = (profile, expiresAt, incomplete || profile.Incomplete || profile.Aggregate?.InterruptedCalls > 0 || profile.Calls.Any(c => c.Outcome == "interrupted")
                    || profile.Aggregate?.Activities?.Values.Any(a => a.Interrupted > 0) == true || profile.Activities?.Any(a => a.Outcome == "interrupted") == true);
        }
        return Task.CompletedTask;
    }
    public Task<ModelProfilePage> ReadModelProfileAsync(Guid runId, long afterSequence, int count, DateTimeOffset now, CancellationToken ct)
    {
        BeforeModelProfileRead?.Invoke(ct);
        ct.ThrowIfCancellationRequested();
        if (ThrowOnModelProfileRead) throw new InvalidOperationException("profile read failed");
        lock (_Profiles)
        {
            if (!_Profiles.TryGetValue(runId, out var entry))
                return Task.FromResult(new ModelProfilePage("legacy-unavailable", runId, null, 0, false, false, [], null));
            var snapshot = entry.Snapshot;
            var expired = entry.Expires <= now;
            var sequences = expired ? [] : snapshot.Calls.Select(c => c.Sequence).Concat((snapshot.Activities ?? []).Select(a => a.Sequence))
                .Where(s => s > afterSequence).Order().Take(count + 1).ToArray();
            var more = sequences.Length > count;
            var retained = sequences.Take(count).ToHashSet();
            var calls = snapshot.Calls.Where(c => retained.Contains(c.Sequence)).ToArray();
            var activities = (snapshot.Activities ?? []).Where(a => retained.Contains(a.Sequence)).ToArray();
            return Task.FromResult(new ModelProfilePage(expired ? "expired" : entry.Incomplete ? "incomplete" :
                snapshot.Truncated ? "truncated" : snapshot.ObservedCalls == 0 && snapshot.Aggregate?.Activities?.Count is not > 0 ? "empty" : "available", runId,
                snapshot.ProfileId, snapshot.ObservedCalls, snapshot.Truncated, entry.Incomplete, calls,
                more ? sequences[count - 1] : null, SchemaVersion: snapshot.SchemaVersion, Aggregate: snapshot.Aggregate,
                Finalized: entry.Expires is not null, Activities: activities));
        }
    }
    public Task<int> ExpireModelProfilesAsync(DateTimeOffset now, int batchSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ThrowOnModelProfileCleanup) throw new InvalidOperationException("profile cleanup failed");
        lock (_Profiles)
        {
            var expired = _Profiles.Where(p => p.Value.Expires <= now && (p.Value.Snapshot.Calls.Count > 0 || p.Value.Snapshot.Activities?.Count > 0)).Take(batchSize).ToArray();
            foreach (var entry in expired) _Profiles[entry.Key] = (entry.Value.Snapshot with { Calls = [], Activities = [] }, entry.Value.Expires, entry.Value.Incomplete);
            return Task.FromResult(expired.Length);
        }
    }
}
