using System.Collections.Concurrent;
using ReviewForge.Core.Ports;

namespace ReviewForge.Core.Workspaces;

/// <summary>Caches measured checkout sizes and refreshes stale entries during eviction sweeps.</summary>
internal sealed class CheckoutSizeCache
{
    private static readonly TimeSpan SizeRefreshInterval = TimeSpan.FromHours(1);
    private readonly IWorkspaceFs _Fs;
    private readonly TimeProvider _Clock;
    private readonly ConcurrentDictionary<string, CachedCheckoutSize> _Sizes = new(StringComparer.Ordinal);

    public CheckoutSizeCache(IWorkspaceFs fs, TimeProvider clock)
    {
        _Fs = fs;
        _Clock = clock;
    }

    public long GetCachedSize(string path, DateTimeOffset now)
    {
        if (_Sizes.TryGetValue(path, out var cached) && now - cached.RefreshedAt < SizeRefreshInterval)
        {
            return cached.Bytes;
        }

        try
        {
            var size = DirectorySize(path);
            _Sizes[path] = new CachedCheckoutSize(size, now);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient — a stale measurement beats pretending the checkout is empty.
            return cached?.Bytes ?? 0;
        }
    }

    public void RefreshCachedSize(string path)
    {
        try
        {
            _Sizes[path] = new CachedCheckoutSize(DirectorySize(path), _Clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _Sizes.TryRemove(path, out _);
        }
    }

    public void Remove(string path)
        => _Sizes.TryRemove(path, out _);

    public long DirectorySize(string path)
        => _Fs.EnumerateFilesRecursive(path).Sum(_Fs.GetFileLength);

    private sealed record CachedCheckoutSize(long Bytes, DateTimeOffset RefreshedAt);
}
