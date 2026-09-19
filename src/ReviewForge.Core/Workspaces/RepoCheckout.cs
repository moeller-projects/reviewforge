namespace ReviewForge.Core.Workspaces;

/// <summary>A materialized repository checkout held by a run-scoped lease.</summary>
public sealed class RepoCheckout : IDisposable
{
    private readonly IDisposable _Lease;

    internal RepoCheckout(string path, IDisposable lease)
    {
        Path = path;
        _Lease = lease;
    }

    public string Path { get; }

    public void Dispose() => _Lease.Dispose();
}
