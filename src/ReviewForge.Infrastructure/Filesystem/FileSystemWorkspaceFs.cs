using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Filesystem;

/// <summary>Filesystem adapter for checkout-pool management.</summary>
public sealed class FileSystemWorkspaceFs : IWorkspaceFs
{
    private static readonly TimeSpan[] DeleteRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path).ToArray();

    public string[] EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path).ToArray();

    public string[] EnumerateFilesRecursive(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray();

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => Directory.GetLastWriteTimeUtc(path);

    public DateTime GetCreationTimeUtc(string path) => Directory.GetCreationTimeUtc(path);

    public void SetLastWriteTimeUtc(string path, DateTime timestamp) => Directory.SetLastWriteTimeUtc(path, timestamp);

    public async Task<IDisposable> AcquireExclusiveLockAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return OpenLockFile(path);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }
    }

    public IDisposable? TryAcquireExclusiveLock(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            return OpenLockFile(path);
        }
        catch (IOException ex) when (IsLockContention(ex))
        {
            return null;
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path, recursive);
                Directory.Delete(path, recursive);
                return;
            }
            catch (Exception ex) when (attempt < DeleteRetryDelays.Length && IsLockContention(ex))
            {
                Thread.Sleep(DeleteRetryDelays[attempt]);
            }
        }
    }

    private static FileStream OpenLockFile(string path)
        => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static bool IsLockContention(Exception exception)
        => (exception.HResult & 0xFFFF) is 11 or 32 or 33;

    private static void ClearReadOnlyAttributes(string root, bool recursive)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            var attributes = File.GetAttributes(current);
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparsePoint && (attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(current, attributes & ~FileAttributes.ReadOnly);
            }

            if (!recursive || (attributes & FileAttributes.Directory) == 0 || isReparsePoint)
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                pending.Push(entry);
            }
        }
    }
}