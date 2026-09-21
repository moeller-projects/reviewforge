using System.Diagnostics.CodeAnalysis;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Filesystem;

/// <summary>Thin <c>System.IO</c> adapter for the checkout pool — a pure vendor wrapper.</summary>
[ExcludeFromCodeCoverage]
public sealed class FileSystemWorkspaceFs : IWorkspaceFs
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path).ToArray();

    public string[] EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path).ToArray();

    public string[] EnumerateFilesRecursive(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime timestamp) => Directory.SetLastWriteTimeUtc(path, timestamp);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
}