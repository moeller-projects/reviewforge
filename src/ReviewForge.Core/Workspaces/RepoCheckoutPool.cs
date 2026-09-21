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
    private readonly IWorkspaceFs _Fs;
    private readonly IGitOps _Git;
    private readonly KeyedLockPool _Locks = new();
    private readonly string? _Pat;
    private readonly string _Root;

    public RepoCheckoutPool(IGitOps git, IWorkspaceFs fs, string root, string? pat = null)
    {
        _Git = git;
        _Fs = fs;
        _Root = Path.GetFullPath(root);
        _Pat = pat;
        _Fs.CreateDirectory(Path.Combine(_Root, "checkouts"));
        _Fs.CreateDirectory(Path.Combine(_Root, "mirror"));
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
            _Fs.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_Fs.DirectoryExists(Path.Combine(path, ".git")) &&
                string.Equals(await _Git.GetHeadShaAsync(path, ct).ConfigureAwait(false), headSha, StringComparison.OrdinalIgnoreCase))
            {
                await _Git.EnsureCommitsAsync(path, cloneUrl, baseSha, headSha, _Pat, ct).ConfigureAwait(false);
                _Fs.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return new RepoCheckout(path, new CheckoutLease(lockLease));
            }

            var repoPath = await _Git.CloneOrOpenAsync(cloneUrl, path, _Pat, ct).ConfigureAwait(false);
            await _Git.EnsureCommitsAsync(repoPath, cloneUrl, baseSha, headSha, _Pat, ct).ConfigureAwait(false);
            await _Git.CheckoutAsync(repoPath, headSha, ct).ConfigureAwait(false);
            if (_Fs.DirectoryExists(repoPath))
            {
                _Fs.SetLastWriteTimeUtc(repoPath, DateTime.UtcNow);
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

    internal Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
        => _Git.GetDiffAsync(repoPath, baseSha, headSha, ct, budget);

    public CheckoutEvictionReport Evict(CheckoutEvictionOptions options, TimeProvider clock)
    {
        if (!options.Enabled)
        {
            return new CheckoutEvictionReport(0, 0, 0, 0);
        }

        var checkoutsRoot = Path.Combine(_Root, "checkouts");
        if (!_Fs.DirectoryExists(checkoutsRoot))
        {
            return new CheckoutEvictionReport(0, 0, 0, 0);
        }

        var cutoff = clock.GetUtcNow() - options.MaxAge;
        var scanned = 0;
        var deleted = 0;
        var inUse = 0;
        long bytes = 0;

        foreach (var repoDir in _Fs.EnumerateDirectories(checkoutsRoot))
        {
            var repoId = Path.GetFileName(repoDir);
            var heads = _Fs.EnumerateDirectories(repoDir)
                .Select(path => (Path: path, Head: Path.GetFileName(path), LastWrite: _Fs.GetLastWriteTimeUtc(path)))
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
                    _Fs.DeleteDirectory(path, recursive: true);
                    bytes += size;
                    deleted++;
                    ReviewForgeTelemetry.CheckoutEvicted.Add(1);
                    ReviewForgeTelemetry.CheckoutEvictedBytes.Add(size);
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
                if (_Fs.EnumerateFileSystemEntries(repoDir).Length == 0)
                {
                    _Fs.DeleteDirectory(repoDir, recursive: false);
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

    /// <summary>Number of materialized head checkouts currently on disk.</summary>
    public int CheckoutDirectoryCount()
    {
        var checkoutsRoot = Path.Combine(_Root, "checkouts");
        if (!_Fs.DirectoryExists(checkoutsRoot))
        {
            return 0;
        }

        var count = 0;
        foreach (var repoDir in _Fs.EnumerateDirectories(checkoutsRoot))
        {
            count += _Fs.EnumerateDirectories(repoDir).Count;
        }

        return count;
    }

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

    private long DirectorySize(string path)
        => _Fs.EnumerateFilesRecursive(path).Sum(_Fs.GetFileLength);

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