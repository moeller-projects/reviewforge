namespace ReviewForge.Core.Workspaces;

/// <summary>Reference-counted keyed semaphores that remove themselves when idle.</summary>
public sealed class KeyedLockPool
{
    private readonly object _Gate = new();
    private readonly Dictionary<string, Entry> _Entries = new(StringComparer.Ordinal);

    public async Task<Lease?> AcquireAsync(string key, CancellationToken ct)
    {
        var entry = Retain(key);
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            Abandon(key, entry);
            throw;
        }
    }

    public Lease? TryAcquire(string key)
    {
        var entry = Retain(key);
        if (!entry.Semaphore.Wait(0))
        {
            Abandon(key, entry);
            return null;
        }

        return new Lease(this, key, entry);
    }

    internal int Count
    {
        get
        {
            lock (_Gate)
            {
                return _Entries.Count;
            }
        }
    }

    private Entry Retain(string key)
    {
        lock (_Gate)
        {
            if (!_Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _Entries.Add(key, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void Abandon(string key, Entry entry)
    {
        lock (_Gate)
        {
            entry.References--;
            RemoveIfIdle(key, entry);
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        lock (_Gate)
        {
            entry.References--;
            RemoveIfIdle(key, entry);
        }
    }

    private void RemoveIfIdle(string key, Entry entry)
    {
        if (entry.References == 0 && _Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _Entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int References { get; set; }
    }

    public sealed class Lease : IDisposable
    {
        private readonly KeyedLockPool _Owner;
        private readonly string _Key;
        private readonly Entry _Entry;
        private int _Disposed;

        internal Lease(KeyedLockPool owner, string key, Entry entry)
        {
            _Owner = owner;
            _Key = key;
            _Entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                _Owner.Release(_Key, _Entry);
            }
        }
    }
}
