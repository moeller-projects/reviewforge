using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
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

    /// <summary>Kind dimension for checkout metrics: every acquisition, active-lease gauge
    /// and eviction recording is split into pooled (shared per-head) and private (run-scoped)
    /// series so writable leases are visible on dashboards.</summary>
    private static TagList PooledKind => new() {{ReviewForgeTelemetry.TagKind, "pooled"}};

    private static TagList PrivateKind => new() {{ReviewForgeTelemetry.TagKind, "private"}};

    // path -> measured size + refresh stamp. Sized once at materialization and refreshed
    // lazily by the eviction sweep when older than an hour; the acquire/reuse path never
    // walks the tree (P2-32) — drift from targeted fetches is MB-scale against a GB budget.
    private static readonly TimeSpan SizeRefreshInterval = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, CachedCheckoutSize> _SizeCache = new(StringComparer.Ordinal);
    private readonly TimeProvider _Clock;

    // Heads whose mirror prefetch completed during a discovery sweep. The next acquire tags
    // its duration measurement with "warmed" (one-shot, TryRemove) so warmup wins are measurable.
    private readonly ConcurrentDictionary<string, byte> _WarmedHeads = new(StringComparer.Ordinal);

    private readonly ILogger<RepoCheckoutPool>? _Logger;

    public RepoCheckoutPool(
        IGitOps git, IWorkspaceFs fs, string root, string? pat = null, TimeProvider? clock = null,
        ILogger<RepoCheckoutPool>? logger = null)
    {
        _Git = git;
        _Fs = fs;
        _Root = Path.GetFullPath(root);
        _Pat = pat;
        _Clock = clock ?? TimeProvider.System;
        _Logger = logger;
        _Fs.CreateDirectory(Path.Combine(_Root, "checkouts"));
        _Fs.CreateDirectory(Path.Combine(_Root, "mirror"));
        _Fs.CreateDirectory(Path.Combine(_Root, "private"));
        _Fs.CreateDirectory(Path.Combine(_Root, "locks"));
    }

    public async Task<RepoCheckout> AcquireAsync(
        string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var key = CheckoutKey(repositoryId, headSha);
        var warmed = _WarmedHeads.TryRemove(key, out _); // one-shot warmup attribution
        var lockLease = await _Locks.AcquireAsync(key, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("checkout lock acquisition returned no lease");
        ReviewForgeTelemetry.CheckoutActive.Add(1, PooledKind);
        try
        {
            var path = CheckoutPath(repositoryId, headSha);
            _Fs.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_Fs.DirectoryExists(Path.Combine(path, ".git")) &&
                string.Equals(await _Git.GetHeadShaAsync(path, ct).ConfigureAwait(false), headSha, StringComparison.OrdinalIgnoreCase))
            {
                await _Git.EnsureCommitsAsync(path, cloneUrl, baseSha, headSha, _Pat, ct).ConfigureAwait(false);
                // No size refresh here: the eviction sweep re-measures entries older than
                // SizeRefreshInterval — the per-run full walk is not worth MB-scale drift.
                _Fs.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}, {ReviewForgeTelemetry.TagWarmed, warmed}});
                return new RepoCheckout(path, new CheckoutLease(lockLease));
            }

            // Clone path: this acquire owns the directory. If anything fails, remove the
            // partial checkout so the next run for this head is not poisoned for days,
            // then retry the clone exactly once (transient network failures self-heal).
            // No retry for the reuse fast-path above: a corrupt existing checkout is a
            // different failure and is surfaced immediately without deleting anything.
            for (var attempt = 1;; attempt++)
            {
                try
                {
                    var repoPath = await _Git.CloneOrOpenAsync(cloneUrl, path, _Pat, ct).ConfigureAwait(false);
                    await _Git.EnsureCommitsAsync(repoPath, cloneUrl, baseSha, headSha, _Pat, ct).ConfigureAwait(false);
                    await _Git.CheckoutAsync(repoPath, headSha, ct).ConfigureAwait(false);
                    if (_Fs.DirectoryExists(repoPath))
                    {
                        _Fs.SetLastWriteTimeUtc(repoPath, DateTime.UtcNow);
                    }

                    RefreshCachedSize(repoPath);

                    ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}, {ReviewForgeTelemetry.TagWarmed, warmed}});
                    return new RepoCheckout(repoPath, new CheckoutLease(lockLease));
                }
                catch (Exception)
                {
                    // Remove the partial dir every time it is left behind — including the
                    // final attempt — then retry the clone exactly once.
                    TryDeletePartialCheckout(path);
                    if (attempt < 2)
                    {
                        continue;
                    }

                    throw;
                }
            }
        }
        catch
        {
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}, {ReviewForgeTelemetry.TagWarmed, warmed}});
            ReviewForgeTelemetry.CheckoutActive.Add(-1, PooledKind);
            lockLease.Dispose();
            throw;
        }
    }

    internal Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
        => _Git.GetDiffAsync(repoPath, baseSha, headSha, ct, budget);

    /// <summary>Run-scoped writable checkout for CommitOnHead runs: a mirror-local clone into
    /// {root}/private/{runId}, protected by process-local and OS-backed locks for its whole
    /// lifetime and deleted on lease disposal. The pooled per-head checkouts never see writes —
    /// CommitOnHead edits live here until the push, then the tree is garbage.</summary>
    public async Task<RepoCheckout> AcquirePrivateAsync(
        Guid runId, string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var path = PrivatePath(runId);
        var lockLease = await AcquirePrivateLockAsync(runId, ct).ConfigureAwait(false);
        ReviewForgeTelemetry.CheckoutActive.Add(1, PrivateKind);
        try
        {
            // Same run id can never recur; a leftover means a crashed earlier attempt whose
            // lock is free — remove it before cloning.
            if (_Fs.DirectoryExists(path))
            {
                _Fs.DeleteDirectory(path, recursive: true);
            }

            _Fs.CreateDirectory(Path.GetDirectoryName(path)!);
            // The mirror path must be passed explicitly: MirrorPath derives the shared mirror
            // from a checkouts-root layout that {root}/private/{runId} does not have.
            var repoPath = await _Git.CloneOrOpenAsync(cloneUrl, path, _Pat, ct, MirrorPath(repositoryId)).ConfigureAwait(false);
            await _Git.EnsureCommitsAsync(repoPath, cloneUrl, baseSha, headSha, _Pat, ct).ConfigureAwait(false);
            await _Git.CheckoutAsync(repoPath, headSha, ct).ConfigureAwait(false); // detached at headSha
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
            return new RepoCheckout(repoPath, new PrivateCheckoutLease(lockLease, this, path));
        }
        catch
        {
            TryDeletePrivateCheckout(path);
            ReviewForgeTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
            ReviewForgeTelemetry.CheckoutActive.Add(-1, PrivateKind);
            lockLease.Dispose();
            throw;
        }
    }

    internal string PrivatePath(Guid runId)
        => Path.Combine(_Root, "private", runId.ToString("N"));

    internal static string PrivateLockKey(Guid runId) => $"private-{runId:N}";

    private async Task<IDisposable> AcquirePrivateLockAsync(Guid runId, CancellationToken ct)
    {
        var name = runId.ToString("N");
        var processLease = await _Locks.AcquireAsync(PrivateLockKey(runId), ct).ConfigureAwait(false)
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

    private IDisposable? TryAcquirePrivateLock(string name)
    {
        var processLease = _Locks.TryAcquire($"private-{name}");
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

    private string PrivateLockPath(string name)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
        return Path.Combine(_Root, "locks", $"private-{digest}.lock");
    }

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

    /// <summary>Loop-guard input for stage 3: author/message of the checked-out head commit.</summary>
    internal Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct)
        => _Git.GetCommitInfoAsync(repoPath, commitSha, ct);

    /// <summary>Best-effort loop-guard input for the discovery filter: the head commit's
    /// author/message from the warmed mirror; null when this repo has no mirror yet.</summary>
    public Task<TipCommitInfo?> GetMirrorCommitInfoAsync(string repositoryId, string headSha, CancellationToken ct)
    {
        var mirror = MirrorPath(repositoryId);
        return _Fs.DirectoryExists(mirror)
            ? _Git.GetCommitInfoAsync(mirror, headSha, ct)
            : Task.FromResult<TipCommitInfo?>(null);
    }

    /// <summary>Startup recovery: private checkouts owned by this process cannot be live.
    /// An OS-backed run lock protects against deleting another process's active checkout.</summary>
    public int ReapOrphanedPrivateCheckouts()
    {
        var privateRoot = Path.Combine(_Root, "private");
        if (!_Fs.DirectoryExists(privateRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var dir in _Fs.EnumerateDirectories(privateRoot))
        {
            using var lease = TryAcquirePrivateLock(Path.GetFileName(dir));
            if (lease is null)
            {
                continue; // another host holds it — never touch a live run's checkout
            }

            try
            {
                var size = DirectorySize(dir);
                _Fs.DeleteDirectory(dir, recursive: true);
                deleted++;
                ReviewForgeTelemetry.CheckoutEvicted.Add(
                    1, new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
                ReviewForgeTelemetry.CheckoutEvictedBytes.Add(
                    size, new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _Logger?.LogWarning(ex, "startup recovery: could not delete private checkout {Path}", dir);
            }
            finally
            {
                lease.Dispose();
            }
        }

        return deleted;
    }

    /// <summary>Disposal-time delete of a private checkout; failures are logged, never thrown —
    /// a dispose exception would mask the run's real outcome. The sweep reaps by age+lock.</summary>
    private void TryDeletePrivateCheckout(string path)
    {
        try
        {
            var privateRoot = Path.GetFullPath(Path.Combine(_Root, "private"));
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(privateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !_Fs.DirectoryExists(fullPath))
            {
                return;
            }

            _Fs.DeleteDirectory(fullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _Logger?.LogWarning(ex, "private checkout {Path} could not be deleted on dispose; the sweep will reap it", path);
        }
    }

    public CheckoutEvictionReport Evict(CheckoutEvictionOptions options, TimeProvider clock)
    {
        if (!options.Enabled)
        {
            return new CheckoutEvictionReport(0, 0, 0, 0);
        }

        var scanned = 0;
        var deleted = 0;
        var inUse = 0;
        var failed = 0;
        var failureDetails = new List<string>();
        long bytes = 0;

        // Private pass — run-scoped checkouts ({root}/private/{runId}): deleted when BOTH the
        // directory is older than Checkout:PrivateMaxAgeMinutes AND its "private-{runId}" lock is free (a held
        // lock = live run = never touched). Independent of the pooled phases below: a missing
        // pooled root must not skip private reaping.
        var privateRoot = Path.Combine(_Root, "private");
        if (_Fs.DirectoryExists(privateRoot))
        {
            foreach (var dir in _Fs.EnumerateDirectories(privateRoot))
            {
                scanned++;
                if (clock.GetUtcNow().UtcDateTime - _Fs.GetCreationTimeUtc(dir)
                    <= TimeSpan.FromMinutes(options.PrivateMaxAgeMinutes))
                {
                    continue; // too young to reap regardless of lock state
                }

                using var privateLease = TryAcquirePrivateLock(Path.GetFileName(dir));
                if (privateLease is null)
                {
                    inUse++;
                    continue;
                }

                try
                {
                    var size = DirectorySize(dir);
                    _Fs.DeleteDirectory(dir, recursive: true);
                    bytes += size;
                    deleted++;
                    ReviewForgeTelemetry.CheckoutEvicted.Add(
                        1, new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
                    ReviewForgeTelemetry.CheckoutEvictedBytes.Add(size, PrivateKind);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    failureDetails.Add($"{dir}: {ex.GetType().Name}");
                }
                finally
                {
                    privateLease.Dispose();
                }
            }
        }

        var checkoutsRoot = Path.Combine(_Root, "checkouts");
        if (!_Fs.DirectoryExists(checkoutsRoot))
        {
            return new CheckoutEvictionReport(scanned, deleted, inUse, bytes)
            {
                Failed = failed,
                FailureDetails = failureDetails,
            };
        }

        var cutoff = clock.GetUtcNow() - options.MaxAge;
        var survivors =
            new List<(string Path, string RepoId, string Head, DateTime LastWrite, long Size, bool DeletionFailed)>();

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
                    survivors.Add((path, repoId, head, lastWrite, GetCachedSize(path, clock.GetUtcNow()), DeletionFailed: false));
                    continue;
                }

                using var evictionLease = _Locks.TryAcquire(EncodedCheckoutKey(repoId, head));
                if (evictionLease is null)
                {
                    inUse++;
                    survivors.Add((path, repoId, head, lastWrite, GetCachedSize(path, clock.GetUtcNow()), DeletionFailed: false));
                    continue;
                }

                var size = 0L;
                try
                {
                    size = DirectorySize(path);
                    _Fs.DeleteDirectory(path, recursive: true);
                    _SizeCache.TryRemove(path, out _);
                    bytes += size;
                    deleted++;
                    ReviewForgeTelemetry.CheckoutEvicted.Add(
                        1, new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}});
                    ReviewForgeTelemetry.CheckoutEvictedBytes.Add(size, PooledKind);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    failureDetails.Add($"{path}: {ex.GetType().Name}");
                    survivors.Add((
                        path,
                        repoId,
                        head,
                        lastWrite,
                        size > 0 ? size : GetCachedSize(path, clock.GetUtcNow()),
                        DeletionFailed: true));
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

        // Phase 2 — global disk budget: evict least-recently-used cross-repo until under budget.
        var totalBytes = survivors.Sum(s => s.Size);
        if (options.MaxTotalBytes > 0 && totalBytes > options.MaxTotalBytes)
        {
            foreach (var candidate in survivors.OrderBy(s => s.LastWrite))
            {
                if (totalBytes <= options.MaxTotalBytes)
                {
                    break;
                }

                if (candidate.DeletionFailed)
                {
                    continue; // account for it, but do not repeat a failed delete in this sweep
                }

                using var evictionLease = _Locks.TryAcquire(EncodedCheckoutKey(candidate.RepoId, candidate.Head));
                if (evictionLease is null)
                {
                    inUse++;
                    continue; // never evict an in-use checkout, even over budget
                }

                try
                {
                    _Fs.DeleteDirectory(candidate.Path, recursive: true);
                    _SizeCache.TryRemove(candidate.Path, out _);
                    bytes += candidate.Size;
                    totalBytes -= candidate.Size;
                    deleted++;
                    ReviewForgeTelemetry.CheckoutEvicted.Add(
                        1, new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}});
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    failureDetails.Add($"{candidate.Path}: {ex.GetType().Name}");
                }
                finally
                {
                    evictionLease.Dispose();
                }
            }
        }

        return new CheckoutEvictionReport(scanned, deleted, inUse, bytes)
        {
            BytesRemaining = totalBytes,
            Failed = failed,
            FailureDetails = failureDetails,
        };
    }

    /// <summary>Cached size of a checkout; measured once and refreshed by the sweep when
    /// older than <see cref="SizeRefreshInterval"/> (P2-32).</summary>
    private long GetCachedSize(string path, DateTimeOffset now)
    {
        if (_SizeCache.TryGetValue(path, out var cached) && now - cached.RefreshedAt < SizeRefreshInterval)
        {
            return cached.Bytes;
        }

        try
        {
            var size = DirectorySize(path);
            _SizeCache[path] = new CachedCheckoutSize(size, now);
            return size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Transient — a stale measurement beats pretending the checkout is empty.
            return cached?.Bytes ?? 0;
        }
    }

    private void RefreshCachedSize(string path)
    {
        try
        {
            _SizeCache[path] = new CachedCheckoutSize(DirectorySize(path), _Clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _SizeCache.TryRemove(path, out _);
        }
    }

    private sealed record CachedCheckoutSize(long Bytes, DateTimeOffset RefreshedAt);

    internal string CheckoutPath(string repositoryId, string headSha)
        => Path.Combine(_Root, "checkouts", KeyComponent(repositoryId), KeyComponent(headSha));

    /// <summary>True when a checkout directory for this repo/head already exists on disk
    /// (path-existence only). Used to skip mirror warmup for heads that need no clone.</summary>
    public bool HasCheckout(string repositoryId, string headSha)
        => _Fs.DirectoryExists(CheckoutPath(repositoryId, headSha));

    /// <summary>
    /// Best-effort prefetch of base/head commits into the shared mirror so a later acquire's
    /// clone skips the origin fetch. The adapter creates the mirror under its own per-mirror
    /// lock when this repo has never been checked out. Throws on failure; callers swallow —
    /// the run's own fetch remains the correctness path.
    /// </summary>
    public Task WarmupAsync(string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
        => _Git.WarmupMirrorAsync(MirrorPath(repositoryId), cloneUrl, baseSha, headSha, _Pat, ct);

    /// <summary>Records that a sweep warmup completed for this head; the next acquire tags
    /// its duration measurement as warmed (one-shot attribution).</summary>
    public void MarkWarmed(string repositoryId, string headSha)
        => _WarmedHeads[CheckoutKey(repositoryId, headSha)] = 1;

    // The infrastructure mirror layout mirrors this: <root>/mirror/<repo-dirname>, where the
    // repo directory name is the sanitized KeyComponent (see LibGit2SharpGitOps.MirrorPath).
    internal string MirrorPath(string repositoryId)
        => Path.Combine(_Root, "mirror", KeyComponent(repositoryId));

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

    /// <summary>
    /// Best-effort removal of a checkout dir created by a failed acquire. Deletes only
    /// directories beneath the pool's checkouts root; failures never mask the original error.
    /// </summary>
    private void TryDeletePartialCheckout(string path)
    {
        try
        {
            var checkoutsRoot = Path.GetFullPath(Path.Combine(_Root, "checkouts"));
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(checkoutsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !_Fs.DirectoryExists(fullPath))
            {
                return;
            }

            _Fs.DeleteDirectory(fullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transient native Git handle or AV lock: eviction will reclaim it later.
        }
    }

    private long DirectorySize(string path)
        => _Fs.EnumerateFilesRecursive(path).Sum(_Fs.GetFileLength);


    /// <summary>Private-checkout lease: disposal deletes {root}/private/{runId} while the
    /// process-local and OS-backed run locks remain held, then releases both locks.</summary>
    private sealed class PrivateCheckoutLease(IDisposable lease, RepoCheckoutPool owner, string path) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                owner.TryDeletePrivateCheckout(path);
                ReviewForgeTelemetry.CheckoutActive.Add(-1, PrivateKind);
                lease.Dispose();
            }
        }
    }

    private sealed class CheckoutLease(KeyedLockPool.Lease lease) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                ReviewForgeTelemetry.CheckoutActive.Add(-1, PooledKind);
                lease.Dispose();
            }
        }
    }
}