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
    private readonly CheckoutLeaseStore _Leases;
    private readonly string? _Pat;
    private readonly string _Root;
    private readonly MirrorManager _Mirrors;


    private readonly CheckoutSizeCache _Sizes;
    private readonly CheckoutEvictionExecutor _Eviction;

    public RepoCheckoutPool(
        IGitOps git, IWorkspaceFs fs, string root, string? pat = null, TimeProvider? clock = null,
        ILogger<RepoCheckoutPool>? logger = null)
    {
        _Git = git;
        _Fs = fs;
        _Root = Path.GetFullPath(root);
        _Pat = pat;
        _Sizes = new CheckoutSizeCache(_Fs, clock ?? TimeProvider.System);
        _Fs.CreateDirectory(Path.Combine(_Root, "checkouts"));
        _Mirrors = new MirrorManager(_Git, _Fs, _Root, _Pat);
        _Leases = new CheckoutLeaseStore(_Fs, _Root);
        _Fs.CreateDirectory(Path.Combine(_Root, "private"));
        _Fs.CreateDirectory(Path.Combine(_Root, "locks"));
        _Eviction = new CheckoutEvictionExecutor(_Fs, _Root, _Leases, _Sizes, logger);
    }

    public async Task<RepoCheckout> AcquireAsync(
        string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var key = CheckoutKey(repositoryId, headSha);
        var warmed = _Mirrors.TryTakeWarmedHead(repositoryId, headSha); // one-shot warmup attribution
        var lockLease = await _Leases.AcquirePooledAsync(key, ct).ConfigureAwait(false);
        CheckoutTelemetry.CheckoutActive.Add(1, CheckoutEvictionExecutor.PooledKind);
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
                CheckoutTelemetry.CheckoutAcquireMilliseconds.Record(
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

                    _Sizes.RefreshCachedSize(repoPath);

                    CheckoutTelemetry.CheckoutAcquireMilliseconds.Record(
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
            CheckoutTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}, {ReviewForgeTelemetry.TagWarmed, warmed}});
            CheckoutTelemetry.CheckoutActive.Add(-1, CheckoutEvictionExecutor.PooledKind);
            lockLease.Dispose();
            throw;
        }
    }

    internal Task<string> GetDiffAsync(string repoPath, string baseSha, string headSha, CancellationToken ct, DiffBudget? budget = null)
        => _Git.GetDiffAsync(repoPath, baseSha, headSha, ct, budget);

    internal Task<string> GetMergeBaseShaAsync(string repoPath, string firstSha, string secondSha, CancellationToken ct)
        => _Git.GetMergeBaseShaAsync(repoPath, firstSha, secondSha, ct);

    /// <summary>Run-scoped writable checkout for CommitOnHead runs: a mirror-local clone into
    /// {root}/private/{runId}, protected by process-local and OS-backed locks for its whole
    /// lifetime and deleted on lease disposal. The pooled per-head checkouts never see writes —
    /// CommitOnHead edits live here until the push, then the tree is garbage.</summary>
    public async Task<RepoCheckout> AcquirePrivateAsync(
        Guid runId, string repositoryId, string cloneUrl, string baseSha, string headSha, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var path = _Leases.PrivatePath(runId);
        var lockLease = await _Leases.AcquirePrivateAsync(runId, ct).ConfigureAwait(false);
        CheckoutTelemetry.CheckoutActive.Add(1, CheckoutEvictionExecutor.PrivateKind);
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
            CheckoutTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
            return new RepoCheckout(repoPath, new PrivateCheckoutLease(lockLease, _Eviction, path));
        }
        catch
        {
            _Eviction.TryDeletePrivateCheckout(path);
            CheckoutTelemetry.CheckoutAcquireMilliseconds.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
            CheckoutTelemetry.CheckoutActive.Add(-1, CheckoutEvictionExecutor.PrivateKind);
            lockLease.Dispose();
            throw;
        }
    }

    internal string PrivatePath(Guid runId)
        => _Leases.PrivatePath(runId);


    /// <summary>Loop-guard input for stage 3: author/message of the checked-out head commit.</summary>
    internal Task<TipCommitInfo?> GetCommitInfoAsync(string repoPath, string commitSha, CancellationToken ct)
        => _Git.GetCommitInfoAsync(repoPath, commitSha, ct);

    /// <summary>Best-effort loop-guard input for the discovery filter: the head commit's
    /// author/message from the warmed mirror; null when this repo has no mirror yet.</summary>
    public Task<TipCommitInfo?> GetMirrorCommitInfoAsync(string repositoryId, string headSha, CancellationToken ct)
        => _Mirrors.GetCommitInfoAsync(repositoryId, headSha, ct);

    /// <summary>Startup recovery: private checkouts owned by this process cannot be live.
    /// An OS-backed run lock protects against deleting another process's active checkout.</summary>
    public int ReapOrphanedPrivateCheckouts()
        => _Eviction.ReapOrphanedPrivateCheckouts();

    public CheckoutEvictionReport Evict(CheckoutEvictionOptions options, TimeProvider clock)
        => _Eviction.Evict(options, clock);

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
        => _Mirrors.WarmupAsync(repositoryId, cloneUrl, baseSha, headSha, ct);

    /// <summary>Records that a sweep warmup completed for this head; the next acquire tags
    /// its duration measurement as warmed (one-shot attribution).</summary>
    public void MarkWarmed(string repositoryId, string headSha)
        => _Mirrors.MarkWarmed(repositoryId, headSha);

    internal string MirrorPath(string repositoryId)
        => _Mirrors.MirrorPath(repositoryId);

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

    internal static string EncodedCheckoutKey(string repositoryComponent, string headComponent)
        => $"{repositoryComponent}:{headComponent}";

    internal static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    internal static string KeyComponent(string id)
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


    /// <summary>Private-checkout lease: disposal deletes {root}/private/{runId} while the
    /// process-local and OS-backed run locks remain held, then releases both locks.</summary>
    private sealed class PrivateCheckoutLease(IDisposable lease, CheckoutEvictionExecutor owner, string path) : IDisposable
    {
        private int _Disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _Disposed, 1) == 0)
            {
                owner.TryDeletePrivateCheckout(path);
                CheckoutTelemetry.CheckoutActive.Add(-1, CheckoutEvictionExecutor.PrivateKind);
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
                CheckoutTelemetry.CheckoutActive.Add(-1, CheckoutEvictionExecutor.PooledKind);
                lease.Dispose();
            }
        }
    }
}