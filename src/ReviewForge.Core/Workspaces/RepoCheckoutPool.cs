using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private readonly KeyedLockPool _Locks = new();
    private readonly string? _Pat;
    private readonly string _Root;

    public RepoCheckoutPool(IGitOps git, string root, string? pat = null)
    {
        _Git = git;
        _Root = Path.GetFullPath(root);
        _Pat = pat;
        Directory.CreateDirectory(Path.Combine(_Root, "checkouts"));
        Directory.CreateDirectory(Path.Combine(_Root, "mirror"));
    }

    public async Task<RepoCheckout> AcquireAsync(
        string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var key = CheckoutKey(repositoryId, headSha);
        var lockLease = await _Locks.AcquireAsync(key, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("checkout lock acquisition returned no lease");
        ReviewForgeTelemetry.CheckoutActive.Add(1);
        try
        {
            var path = CheckoutPath(repositoryId, headSha);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (Directory.Exists(Path.Combine(path, ".git")) &&
                string.Equals(_Git.GetHeadSha(path), headSha, StringComparison.OrdinalIgnoreCase))
            {
                _Git.EnsureCommits(path, cloneUrl, baseSha, headSha, _Pat);
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return new RepoCheckout(path, new CheckoutLease(lockLease));
            }

            var repoPath = _Git.CloneOrOpen(cloneUrl, path, _Pat);
            _Git.EnsureCommits(repoPath, cloneUrl, baseSha, headSha, _Pat);
            _Git.Checkout(repoPath, headSha);
            if (Directory.Exists(repoPath))
            {
                Directory.SetLastWriteTimeUtc(repoPath, DateTime.UtcNow);
            }

            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new RepoCheckout(repoPath, new CheckoutLease(lockLease));
        }
        catch
        {
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            ReviewForgeTelemetry.CheckoutActive.Add(-1);
            lockLease.Dispose();
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

                using var evictionLease = _Locks.TryAcquire(EncodedCheckoutKey(repoId, head));
                if (evictionLease is null)
                {
                    inUse++;
                    continue;
                }

                try
                {
                    var size = DirectorySize(path);
                    Directory.Delete(path, recursive: true);
                    bytes += size;
                    deleted++;
                    ReviewForgeTelemetry.CheckoutEvicted.Add(1);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A transient native Git handle will be retried on the next sweep.
                }
                finally
                {
                    evictionLease.Dispose();
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(repoDir).Any())
                {
                    Directory.Delete(repoDir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A concurrent acquire or native Git handle will be retried next sweep.
            }
        }

        return new CheckoutEvictionReport(scanned, deleted, inUse, bytes);
    }

    internal string CheckoutPath(string repositoryId, string headSha)
        => Path.Combine(_Root, "checkouts", KeyComponent(repositoryId), KeyComponent(headSha));

    internal static string CheckoutKey(string repositoryId, string headSha)
        => EncodedCheckoutKey(KeyComponent(repositoryId), KeyComponent(headSha));

    private static string EncodedCheckoutKey(string repositoryComponent, string headComponent)
        => $"{repositoryComponent}:{headComponent}";

    internal static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static string KeyComponent(string id)
    {
        var readable = Sanitize(id);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12].ToLowerInvariant();
        return $"{readable}-{hash}";
    }

    private static long DirectorySize(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private sealed class CheckoutLease(KeyedLockPool.Lease lease) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                ReviewForgeTelemetry.CheckoutActive.Add(-1);
                lease.Dispose();
            }
        }
    }
}