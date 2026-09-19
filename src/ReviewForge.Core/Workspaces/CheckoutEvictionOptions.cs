namespace ReviewForge.Core.Workspaces;

public sealed class CheckoutEvictionOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(3);
    public int MaxCheckoutsPerRepo { get; init; } = 10;
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);
}

public sealed record CheckoutEvictionReport(int Scanned, int Deleted, int SkippedInUse, long BytesFreed);
