using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Circuit-only bounded worklist context. Never serialized to browser storage.
/// A new circuit deliberately starts a new window; source snapshots remain subject to refresh.</summary>
public sealed class DashboardWorklistState
{
    public RunWorklistResponse? Data;
    public IReadOnlyList<RunWorklistItem> Items = [];
    public bool AttentionOnly = true, HasMore, RetainedHistory, ReplaceHistoryOnLoad;
    public int PageCount = 1;
    public string Search = "";
    public DateTimeOffset LastSuccess;
    public double ScrollY;
    public readonly Dictionary<PrKey, bool> PrExpanded = [];
    public readonly Dictionary<(PrKey Pr, string Head), bool> RevisionExpanded = [];
}
