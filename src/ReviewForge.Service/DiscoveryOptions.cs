namespace ReviewForge.Service;

/// <summary>Org-wide sweep configuration: filter rules plus the optional background interval.</summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>Branch short names to consider; matching is case-insensitive.</summary>
    public string[] TargetBranches { get; init; } = ["main", "develop"];

    /// <summary>Creator filter (matches id or name); empty allows all creators.</summary>
    public string[] Creators { get; init; } = [];

    public int MaxEnqueuesPerSweep { get; init; } = 20;

    /// <summary>Background sweep interval; null disables the sweep worker.</summary>
    public TimeSpan? SweepInterval { get; init; }
}