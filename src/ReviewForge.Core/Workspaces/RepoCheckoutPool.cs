using System.Collections.Concurrent;
using System.Diagnostics;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
namespace ReviewForge.Core.Workspaces;

/// <summary>
/// Materializes one checkout per repository/head pair. The per-key lease remains held
/// for the entire review run, preventing both duplicate materialization and eviction races.
/// </summary>
public sealed class RepoCheckoutPool
{
    private readonly IGitOps _Git;
    private readonly string _Root;
    private readonly string? _Pat;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _Locks = new(StringComparer.Ordinal);

    public RepoCheckoutPool(IGitOps git, string root, string? pat = null)
    {
        _Git = git;
        _Root = Path.GetFullPath(root);
        _Pat = pat;
        Directory.CreateDirectory(Path.Combine(_Root, "checkouts"));
        Directory.CreateDirectory(Path.Combine(_Root, "mirror"));
    }

    public async Task<RepoCheckout> AcquireAsync(
        string repositoryId, string cloneUrl, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var key = CheckoutKey(repositoryId, headSha);
        var semaphore = _Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        ReviewForgeTelemetry.CheckoutActive.Add(1);
        try
        {
            var path = CheckoutPath(repositoryId, headSha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.Exists(Path.Combine(path, ".git")) &&
                string.Equals(_Git.GetHeadSha(path), headSha, StringComparison.OrdinalIgnoreCase))
            {
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return new RepoCheckout(path, new Lease(semaphore));
            }

            var repoPath = _Git.CloneOrOpen(cloneUrl, path, _Pat);
            _Git.Checkout(repoPath, headSha);
            if (Directory.Exists(repoPath))
            {
                Directory.SetLastWriteTimeUtc(repoPath, DateTime.UtcNow);
            }
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new RepoCheckout(repoPath, new Lease(semaphore));
        }
        catch
        {
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            ReviewForgeTelemetry.CheckoutActive.Add(-1);
            semaphore.Release();
            throw;
        }
    }

    internal string GetDiff(string repoPath, string baseSha, string headSha)
        => _Git.GetDiff(repoPath, baseSha, headSha);
    public CheckoutEvictionReport Evict(CheckoutEvictionOptions options, TimeProvider clock)
    {
        if (!options.Enabled)
        {
            return new CheckoutEvictionReport(0, 0, 0, 0);
        }

        var checkoutsRoot = Path.Combine(_Root, "checkouts");
        if (!Directory.Exists(checkoutsRoot))
        {
            return new CheckoutEvictionReport(0, 0, 0, 0);
        }

        var cutoff = clock.GetUtcNow() - options.MaxAge;
        var scanned = 0;
        var deleted = 0;
        var inUse = 0;
        long bytes = 0;

        foreach (var repoDir in Directory.EnumerateDirectories(checkoutsRoot))
        {
            var repoId = Path.GetFileName(repoDir);
            var heads = Directory.EnumerateDirectories(repoDir)
                .Select(path => (Path: path, Head: Path.GetFileName(path), LastWrite: Directory.GetLastWriteTimeUtc(path)))
                .OrderByDescending(item => item.LastWrite)
                .ToArray();

            for (var i = 0; i < heads.Length; i++)
            {
                scanned++;
                var (path, head, lastWrite) = heads[i];
                if (i < options.MaxCheckoutsPerRepo && lastWrite >= cutoff.UtcDateTime)
                {
                    continue;
                }

                if (!TryLockForEviction(repoId, head))
                {
                    inUse++;
                    continue;
                }

                try
                {
                    var size = DirectorySize(path);
                    try
                    {
                        Directory.Delete(path, recursive: true);
                        bytes += size;
                        deleted++;
                        ReviewForgeTelemetry.CheckoutEvicted.Add(1);
                    }
                    catch (IOException)
                    {
                        // A transient native Git handle will be retried on the next sweep.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // A transient native Git handle will be retried on the next sweep.
                    }
                }
                finally
                {
                    UnlockForEviction(repoId, head);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(repoDir).Any())
            {
                Directory.Delete(repoDir);
            }
        }

        return new CheckoutEvictionReport(scanned, deleted, inUse, bytes);
    }

    internal string CheckoutPath(string repositoryId, string headSha)
        => Path.Combine(_Root, "checkouts", Sanitize(repositoryId), Sanitize(headSha));

    internal static string CheckoutKey(string repositoryId, string headSha)
        => $"{Sanitize(repositoryId)}:{Sanitize(headSha)}";

    internal static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    internal bool TryLockForEviction(string repositoryId, string headSha)
        => _Locks.GetOrAdd(CheckoutKey(repositoryId, headSha), _ => new SemaphoreSlim(1, 1)).Wait(0);

    internal void UnlockForEviction(string repositoryId, string headSha)
        => _Locks[CheckoutKey(repositoryId, headSha)].Release();

    private static long DirectorySize(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                ReviewForgeTelemetry.CheckoutActive.Add(-1);
                semaphore.Release();
            }
        }
    }
}
