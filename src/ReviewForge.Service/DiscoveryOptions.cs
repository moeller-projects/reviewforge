namespace ReviewForge.Service;

/// <summary>Org-wide sweep configuration: filter rules plus the optional background interval.</summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>Branch short names to consider; matching is case-insensitive.</summary>
    public string[] TargetBranches { get; init; } = ["main", "develop"];

    /// <summary>Creator filter (matches id or name). REQUIRED when SweepInterval is set,
    /// unless AllowAllCreators is explicitly true.</summary>
    public string[] Creators { get; init; } = [];

    /// <summary>Explicit opt-out of the creator allowlist. Reviews PRs from any author,
    /// including external contributors — only enable with prompt-injection hardening in place.</summary>
    public bool AllowAllCreators { get; init; }

    public int MaxEnqueuesPerSweep { get; init; } = 20;

    /// <summary>
    /// Max concurrent ADO/store round-trips during a sweep. 1 restores fully sequential
    /// behavior. Keep low (2–4) on PATs shared with other tooling; default 8 stays far
    /// below ADO per-user throttling limits.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 8;

    /// <summary>Background sweep interval; null disables the sweep worker.</summary>
    public TimeSpan? SweepInterval { get; init; }

    /// <summary>Backoff base after a failed run at the same head (doubles per consecutive failure).</summary>
    public TimeSpan FailureBackoffBase { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Cap for the failure backoff.</summary>
    public TimeSpan FailureBackoffMax { get; init; } = TimeSpan.FromHours(8);
}