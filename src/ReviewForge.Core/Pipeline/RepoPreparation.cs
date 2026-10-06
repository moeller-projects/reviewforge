using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Pipeline;

/// <summary>Repository outputs and overlapping stage work produced before classification.</summary>
public sealed record RepoPreparation : IDisposable
{
    private CancellationTokenSource? _overlapCts;

    public string? RepoDir { get; init; }
    public string DiffText { get; init; } = string.Empty;
    public DiffIndex? Diff { get; init; }
    public IReadOnlyCollection<string>? ReviewableFiles { get; init; }
    public TipCommitInfo? HeadCommitInfo { get; init; }
    public ThreadsRefreshOverlap? PendingThreadsRefresh { get; init; }
    public DateTimeOffset? RepoPreparedAt { get; init; }
    public Task<string?>? PendingEnrichment { get; init; }

    internal CancellationTokenSource OverlapCts => _overlapCts ??= new();

    public void Dispose()
    {
        OverlapCts.Cancel();
        ObserveFault(PendingThreadsRefresh?.Task);
        ObserveFault(PendingEnrichment);
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>
/// Threads-refresh overlap: the fetch task plus the stamp of when it was launched.
/// The completion stamp determines whether the response arrived at/after repository
/// preparation, the freshness boundary that replaces the serial stage-4 refetch.
/// </summary>
public sealed record ThreadsRefreshOverlap(
    Task<IReadOnlyList<ReviewThread>> Task,
    DateTimeOffset StartedAt)
{
    /// <summary>Utc stamp of the received response; null while genuinely in flight.</summary>
    public DateTimeOffset? CompletedAt { get; internal set; }
}
