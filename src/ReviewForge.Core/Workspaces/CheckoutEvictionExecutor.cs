using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Workspaces;

/// <summary>Executes policy-selected cleanup while holding the corresponding checkout leases.</summary>
internal sealed class CheckoutEvictionExecutor
{
    private readonly IWorkspaceFs _Fs;
    private readonly string _Root;
    private readonly CheckoutLeaseStore _Leases;
    private readonly CheckoutSizeCache _Sizes;
    private readonly ILogger? _Logger;

    internal static TagList PooledKind => new() {{ReviewForgeTelemetry.TagKind, "pooled"}};
    internal static TagList PrivateKind => new() {{ReviewForgeTelemetry.TagKind, "private"}};

    public CheckoutEvictionExecutor(
        IWorkspaceFs fs,
        string root,
        CheckoutLeaseStore leases,
        CheckoutSizeCache sizes,
        ILogger? logger)
    {
        _Fs = fs;
        _Root = root;
        _Leases = leases;
        _Sizes = sizes;
        _Logger = logger;
    }

    public int ReapOrphanedPrivateCheckouts()
    {
        var privateRoot = Path.Combine(_Root, "private");
        if (!_Fs.DirectoryExists(privateRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var dir in CheckoutEvictionPolicy.SelectStartupRecoveryCandidates(_Fs.EnumerateDirectories(privateRoot)))
        {
            using var lease = _Leases.TryAcquirePrivate(Path.GetFileName(dir));
            if (lease is null)
            {
                continue; // another host holds it — never touch a live run's checkout
            }

            try
            {
                var size = _Sizes.DirectorySize(dir);
                _Fs.DeleteDirectory(dir, recursive: true);
                deleted++;
                CheckoutTelemetry.CheckoutEvicted.Add(
                    1, new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
                CheckoutTelemetry.CheckoutEvictedBytes.Add(
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
    internal void TryDeletePrivateCheckout(string path)
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
            var privateDirectories = _Fs.EnumerateDirectories(privateRoot);
            scanned += privateDirectories.Count;
            foreach (var candidate in CheckoutEvictionPolicy.SelectPrivateCandidates(
                         privateDirectories.Select(dir => new PrivateCheckoutMetadata(
                             dir, _Fs.GetCreationTimeUtc(dir), clock.GetUtcNow())),
                         options))
            {
                var dir = candidate.Path;
                using var privateLease = _Leases.TryAcquirePrivate(Path.GetFileName(dir));
                if (privateLease is null)
                {
                    inUse++;
                    continue;
                }

                try
                {
                    var size = _Sizes.DirectorySize(dir);
                    _Fs.DeleteDirectory(dir, recursive: true);
                    bytes += size;
                    deleted++;
                    CheckoutTelemetry.CheckoutEvicted.Add(
                        1, new TagList {{ReviewForgeTelemetry.TagKind, "private"}});
                    CheckoutTelemetry.CheckoutEvictedBytes.Add(size, PrivateKind);
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

        var policyTime = clock.GetUtcNow();
        var survivors =
            new List<(string Path, string RepoId, string Head, DateTime LastWrite, long Size, bool DeletionFailed)>();

        foreach (var repoDir in _Fs.EnumerateDirectories(checkoutsRoot))
        {
            var repoId = Path.GetFileName(repoDir);
            var heads = _Fs.EnumerateDirectories(repoDir)
                .Select(path => (Path: path, Head: Path.GetFileName(path), LastWrite: _Fs.GetLastWriteTimeUtc(path)))
                .OrderByDescending(item => item.LastWrite)
                .ToArray();
            var evictionCandidates = CheckoutEvictionPolicy.SelectPooledCandidates(
                heads.Select(item => new PooledCheckoutMetadata(item.Path, repoId, item.Head, item.LastWrite)).ToArray(),
                options,
                policyTime);
            var candidateIndex = 0;

            for (var i = 0; i < heads.Length; i++)
            {
                scanned++;
                var (path, head, lastWrite) = heads[i];
                if (candidateIndex >= evictionCandidates.Count || evictionCandidates[candidateIndex].Path != path)
                {
                    survivors.Add((path, repoId, head, lastWrite, _Sizes.GetCachedSize(path, clock.GetUtcNow()), DeletionFailed: false));
                    continue;
                }

                candidateIndex++;

                using var evictionLease = _Leases.TryAcquirePooled(RepoCheckoutPool.EncodedCheckoutKey(repoId, head));
                if (evictionLease is null)
                {
                    inUse++;
                    survivors.Add((path, repoId, head, lastWrite, _Sizes.GetCachedSize(path, clock.GetUtcNow()), DeletionFailed: false));
                    continue;
                }

                var size = 0L;
                try
                {
                    size = _Sizes.DirectorySize(path);
                    _Fs.DeleteDirectory(path, recursive: true);
                    _Sizes.Remove(path);
                    bytes += size;
                    deleted++;
                    CheckoutTelemetry.CheckoutEvicted.Add(
                        1, new TagList {{ReviewForgeTelemetry.TagKind, "pooled"}});
                    CheckoutTelemetry.CheckoutEvictedBytes.Add(size, PooledKind);
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
                        size > 0 ? size : _Sizes.GetCachedSize(path, clock.GetUtcNow()),
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
        var budget = CheckoutEvictionPolicy.SelectBudgetCandidates(
            survivors.Select(s => new PooledCheckoutMetadata(
                s.Path, s.RepoId, s.Head, s.LastWrite, s.Size, s.DeletionFailed)).ToArray(),
            options);
        var totalBytes = budget.TotalBytes;
        foreach (var candidate in budget.Candidates)
        {
            if (totalBytes <= options.MaxTotalBytes)
            {
                break;
            }

            using var evictionLease = _Leases.TryAcquirePooled(RepoCheckoutPool.EncodedCheckoutKey(candidate.RepositoryId, candidate.Head));
            if (evictionLease is null)
            {
                inUse++;
                continue; // never evict an in-use checkout, even over budget
            }

            try
            {
                _Fs.DeleteDirectory(candidate.Path, recursive: true);
                _Sizes.Remove(candidate.Path);
                bytes += candidate.Size;
                totalBytes -= candidate.Size;
                deleted++;
                CheckoutTelemetry.CheckoutEvicted.Add(
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

        return new CheckoutEvictionReport(scanned, deleted, inUse, bytes)
        {
            BytesRemaining = totalBytes,
            Failed = failed,
            FailureDetails = failureDetails,
        };
    }
}
