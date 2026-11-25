using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

/// <summary>Safe dashboard projection, deliberately excluding raw exceptions and persisted entities.
/// Null state on an incomplete shell/claim is unknown, not resumed pipeline progress.</summary>
public sealed record DashboardRun(Guid RunId, string? HeadSha, RunKind? Kind, RunState? State,
    DateTimeOffset? EnqueuedAt = null, DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null,
    EnqueueTrigger? Trigger = null, string? CurrentStage = null, int? ProgressPercent = null,
    int? ReviewFindings = null, bool IsClaimed = false, bool IsIncomplete = false, bool IsPending = false)
{
    public IReadOnlyList<string> LoadedStages { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? OutcomeDetail { get; init; }
}

public sealed record DashboardRow(PullRequestInventoryItem PullRequest,
    bool? CurrentHeadReviewed = null, bool? CanRerun = null,
    DashboardRun? LastSuccessfulReview = null, DashboardRun? LatestAttempt = null, DashboardRun? ActiveBlocker = null,
    BasicEligibility? Eligibility = null)
{
    /// <summary>Latest ordinary-review attempt across all heads, including failures before head capture.
    /// LatestAttempt remains the separate current-head attempt; neither implies successful coverage.</summary>
    public DashboardRun? LatestPrAttempt { get; init; }
}

public sealed record DashboardResponse(DateTimeOffset GeneratedAt, DateTimeOffset InventoryFetchedAt,
    bool IsComplete, IReadOnlyList<string> SourceWarnings, IReadOnlyList<DashboardRow> Rows,
    DashboardSummary? Summary = null, bool DiscoveryEnabled = false)
{
    public bool InventoryStale { get; init; }
    public bool InventoryRefreshing { get; init; }
    public bool InventoryRefreshFailed { get; init; }

    /// <summary>Scoped run observations independent of head, for reconciling accepted submissions.
    /// Missing or null states remain unknown; this is not durable attempt history.</summary>
    public IReadOnlyDictionary<Guid, RunState?> RunStates { get; init; } = new Dictionary<Guid, RunState?>();
}

public sealed record DashboardSummary(int OpenPullRequests, int Reviewing, int Reviewed, int NeedsAttention,
    bool IsPartial)
{
    public string HelpText => "Reviewed and Needs attention can overlap after a failed rerun. Completed reviews with findings are not failures. Basic eligibility does not promise scheduling.";
}
