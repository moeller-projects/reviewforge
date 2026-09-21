namespace ReviewForge.Core.Ports;

/// <summary>
/// Filesystem primitives the checkout pool needs, isolated behind a port so the Core
/// layer never performs filesystem I/O directly (ports/adapters rule). Implementations
/// are thin wrappers over <c>System.IO</c>.
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