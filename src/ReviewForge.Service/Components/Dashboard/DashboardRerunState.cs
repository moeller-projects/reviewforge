using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Components.Dashboard;

/// <summary>Page-owned action state shared by grid and details. Snapshots are advisory, never PR reservations.</summary>
public sealed class DashboardRerunState
{
    private readonly HashSet<PrKey> _Pending = [];
    private readonly Dictionary<PrKey, Guid> _Accepted = [];
    private readonly Dictionary<PrKey, DashboardRow> _Rows = [];
    public event Action? Changed;
    public bool AuthenticationExpired { get; private set; }
    public bool AwaitingAccepted => _Accepted.Count != 0;
    public DashboardRow? Current(PrKey key) => _Rows.GetValueOrDefault(key);
    public bool HasInventory { get; private set; }
    public void ExpireAuthentication()
    {
        AuthenticationExpired = true;
        Changed?.Invoke();
    }
    public Guid? AcceptedRun(PrKey key) => _Accepted.TryGetValue(key, out var id) ? id : null;
    public string Context(DashboardRow row)
    {
        var key = row.PullRequest.Candidate.Key;
        if (AuthenticationExpired) return "Session expired. Log in again before rerunning reviews.";
        if (HasInventory && !_Rows.ContainsKey(key)) return "PR is no longer in the retrieved inventory. It may be closed or unavailable.";
        if (_Pending.Contains(key)) return "Confirmation or submission pending for this PR.";
        if (AcceptedRun(key) is { } id) return $"Queued review {id}. Waiting for operational status.";
        row = _Rows.GetValueOrDefault(key, row);
        if (row.ActiveBlocker is not null) return $"Rerun blocked: {DashboardPresentation.Blocker(row)}.";
        return row.CanRerun == true ? "Force an ordinary review on the same head; drafts and safeguards still apply."
            : "Rerun availability unknown. Refresh before trying again.";
    }
    public bool CanBegin(DashboardRow row)
    {
        var key = row.PullRequest.Candidate.Key;
        row = _Rows.GetValueOrDefault(key, row);
        return !AuthenticationExpired && (!HasInventory || _Rows.ContainsKey(key)) && !_Pending.Contains(key) && !_Accepted.ContainsKey(key) && row.CanRerun == true && row.ActiveBlocker is null;
    }
    public bool TryBegin(DashboardRow row)
    {
        if (!CanBegin(row)) return false;
        _Pending.Add(row.PullRequest.Candidate.Key);
        Changed?.Invoke();
        return true;
    }
    public void Accepted(PrKey key, Guid id)
    {
        _Accepted[key] = id;
        Changed?.Invoke();
    }
    public void End(PrKey key)
    {
        _Pending.Remove(key);
        Changed?.Invoke();
    }
    public void Reconcile(DashboardResponse response)
    {
        HasInventory = true;
        var keys = response.Rows.Select(r => r.PullRequest.Candidate.Key).ToHashSet();
        foreach (var key in _Rows.Keys.Concat(_Accepted.Keys).Concat(_Pending).Where(k => !keys.Contains(k)).Distinct().ToArray())
        {
            _Rows.Remove(key);
            _Accepted.Remove(key);
            _Pending.Remove(key);
        }
        foreach (var row in response.Rows)
        {
            var key = row.PullRequest.Candidate.Key;
            _Rows[key] = row;
            if (AcceptedRun(key) is { } id && (row.ActiveBlocker is not null || row.LatestAttempt?.RunId == id ||
                response.RunStates.TryGetValue(id, out var state) && state is RunState.Completed or RunState.Skipped or RunState.Failed))
                _Accepted.Remove(key);
        }
        Changed?.Invoke();
    }
}
