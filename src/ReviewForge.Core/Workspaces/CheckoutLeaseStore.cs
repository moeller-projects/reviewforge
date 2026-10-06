using System.Security.Cryptography;
using System.Text;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Workspaces;

/// <summary>Owns process-local and OS-backed checkout leases for pooled and private checkouts.</summary>
internal sealed class CheckoutLeaseStore
{
    private readonly IWorkspaceFs _Fs;
    private readonly string _Root;
    private readonly KeyedLockPool _Locks = new();

    public CheckoutLeaseStore(IWorkspaceFs fs, string root)
    {
        _Fs = fs;
        _Root = root;
    }

    public async Task<KeyedLockPool.Lease> AcquirePooledAsync(string lockKey, CancellationToken ct)
        => await _Locks.AcquireAsync(lockKey, ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException("checkout lock acquisition returned no lease");

    public KeyedLockPool.Lease? TryAcquirePooled(string lockKey)
        => _Locks.TryAcquire(lockKey);

    public async Task<IDisposable> AcquirePrivateAsync(Guid runId, CancellationToken ct)
    {
        var name = runId.ToString("N");
        var processLease = await _Locks.AcquireAsync(PrivateLockKey(name), ct).ConfigureAwait(false)
                           ?? throw new InvalidOperationException("private checkout lock acquisition returned no lease");
        try
        {
            var fileLease = await _Fs.AcquireExclusiveLockAsync(PrivateLockPath(name), ct).ConfigureAwait(false);
            return new PrivateLockLease(processLease, fileLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

    public IDisposable? TryAcquirePrivate(string name)
    {
        var processLease = _Locks.TryAcquire(PrivateLockKey(name));
        if (processLease is null)
        {
            return null;
        }

        try
        {
            var fileLease = _Fs.TryAcquireExclusiveLock(PrivateLockPath(name));
            if (fileLease is null)
            {
                processLease.Dispose();
                return null;
            }

            return new PrivateLockLease(processLease, fileLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

    public string PrivatePath(Guid runId)
        => Path.Combine(_Root, "private", runId.ToString("N"));

    private string PrivateLockPath(string name)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        return Path.Combine(_Root, "locks", $"private-{digest}.lock");
    }

    private static string PrivateLockKey(string name) => $"private-{name}";

    private sealed class PrivateLockLease(IDisposable processLease, IDisposable fileLease) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                try
                {
                    fileLease.Dispose();
                }
                finally
                {
                    processLease.Dispose();
                }
            }
        }
    }
}
