namespace ReviewForge.Core.Ports;

/// <summary>
/// Filesystem primitives for checkout-pool management and eviction (directory lifecycle,
/// size accounting, timestamps). Implementations are thin wrappers over <c>System.IO</c>.
/// Scope note: this port is deliberately NOT a general filesystem abstraction — read-only
/// access inside a repo checkout (RepoReadTools, anchor validation, path containment) and
/// single-writer run artifacts (findings/{runId}.jsonl) use System.IO directly in Core;
/// those paths are either covered by injected delegates or exercised through real temp
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

    void SetLastWriteTimeUtc(string path, DateTime timestamp);

    void DeleteDirectory(string path, bool recursive);
}