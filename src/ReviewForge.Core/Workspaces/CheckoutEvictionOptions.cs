namespace ReviewForge.Core.Workspaces;

public sealed class CheckoutEvictionOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(3);
    public int MaxCheckoutsPerRepo { get; init; } = 10;
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Global disk budget across ALL checkouts. When the aggregate exceeds this, the
    /// least-recently-used checkouts (by last acquire) are evicted cross-repo until the
    /// total fits. 0 disables the budget (count/age rules still apply).
    /// </summary>
    public long MaxTotalBytes { get; init; } = 20L * 1024 * 1024 * 1024;
}

public sealed record CheckoutEvictionReport(int Scanned, int Deleted, int SkippedInUse, long BytesFreed)
{
    /// <summary>Aggregate checkout bytes on disk after the sweep (budget accounting).</summary>
    public long BytesRemaining { get; init; }

    /// <summary>Deletions that failed (IOException/UnauthorizedAccessException — e.g. a
    /// persistent native Git handle or permission problem). &gt;0 over consecutive sweeps
    /// means stale checkouts are NOT self-healing.</summary>
    public int Failed { get; init; }

    /// <summary>Per-failure detail for the worker log (path + exception type).</summary>
    public IReadOnlyList<string> FailureDetails { get; init; } = [];
}
