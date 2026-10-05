namespace ReviewForge.Core.Ports;

/// <summary>
/// Filesystem primitives for checkout-pool management and eviction (directory lifecycle,
/// size accounting, timestamps, and cross-process private-checkout locks). Implementations
/// are thin wrappers over <c>System.IO</c>. Scope note: this is deliberately NOT a general
/// filesystem abstraction — read-only access inside a repo checkout (RepoReadTools, anchor
/// validation, path containment) and single-writer run artifacts use System.IO directly in
/// Core; those paths are either covered by injected delegates or exercised through real temp
/// directories in tests.
/// </summary>
public interface IWorkspaceFs
{
    void CreateDirectory(string path);

    bool DirectoryExists(string path);

    IReadOnlyList<string> EnumerateDirectories(string path);

    string[] EnumerateFileSystemEntries(string path);

    /// <summary>Recursive file enumeration used for eviction size accounting.</summary>
    string[] EnumerateFilesRecursive(string path);

    long GetFileLength(string path);

    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>Directory creation time (age signal for private-checkout reaping).</summary>
    DateTime GetCreationTimeUtc(string path);

    void SetLastWriteTimeUtc(string path, DateTime timestamp);

    /// <summary>
    /// Acquires an OS-backed exclusive lock on a lock-file path, waiting cancellably while
    /// another process owns it. The returned lease releases the lock on disposal.
    /// </summary>
    Task<IDisposable> AcquireExclusiveLockAsync(string path, CancellationToken ct);

    /// <summary>Attempts to acquire an OS-backed exclusive lock without waiting.</summary>
    IDisposable? TryAcquireExclusiveLock(string path);

    /// <summary>Deletes a directory recursively, handling read-only checkout files.</summary>
    void DeleteDirectory(string path, bool recursive);
}