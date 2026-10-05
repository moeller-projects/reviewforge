using ReviewForge.Infrastructure.Filesystem;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class FileSystemWorkspaceFsTests : IDisposable
{
    private readonly string _Root = Path.Combine(Path.GetTempPath(), "reviewforge-fs-" + Guid.NewGuid().ToString("N"));

    public FileSystemWorkspaceFsTests() => Directory.CreateDirectory(_Root);

    public void Dispose()
    {
        if (Directory.Exists(_Root))
        {
            new FileSystemWorkspaceFs().DeleteDirectory(_Root, recursive: true);
        }
    }

    [Fact]
    public void DeleteDirectory_clears_read_only_attributes_recursively()
    {
        var fs = new FileSystemWorkspaceFs();
        var directory = Path.Combine(_Root, "private", "run");
        var nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        var packedIndex = Path.Combine(nested, "pack-test.idx");
        File.WriteAllText(packedIndex, "idx");
        File.SetAttributes(packedIndex, File.GetAttributes(packedIndex) | FileAttributes.ReadOnly);
        File.SetAttributes(nested, File.GetAttributes(nested) | FileAttributes.ReadOnly);

        fs.DeleteDirectory(directory, recursive: true);

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task DeleteDirectory_retries_a_windows_sharing_violation_until_the_handle_closes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fs = new FileSystemWorkspaceFs();
        var directory = Path.Combine(_Root, "private", "run");
        Directory.CreateDirectory(directory);
        var packedIndex = Path.Combine(directory, "pack-test.idx");
        File.WriteAllText(packedIndex, "idx");
        var held = new FileStream(packedIndex, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var releaseHandle = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80));
            held.Dispose();
        });

        try
        {
            fs.DeleteDirectory(directory, recursive: true);
        }
        finally
        {
            await releaseHandle;
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Exclusive_lock_is_exclusive_across_filesystem_adapter_instances()
    {
        var first = new FileSystemWorkspaceFs();
        var second = new FileSystemWorkspaceFs();
        var lockPath = Path.Combine(_Root, "locks", "private-run.lock");

        using (var held = first.TryAcquireExclusiveLock(lockPath))
        {
            Assert.NotNull(held);
            Assert.Null(second.TryAcquireExclusiveLock(lockPath));
        }

        using var reacquired = second.TryAcquireExclusiveLock(lockPath);
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task Acquire_exclusive_lock_waits_cancellably_and_opens_after_release()
    {
        var first = new FileSystemWorkspaceFs();
        var second = new FileSystemWorkspaceFs();
        var lockPath = Path.Combine(_Root, "locks", "wait.lock");
        using var held = first.TryAcquireExclusiveLock(lockPath);
        Assert.NotNull(held);

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireExclusiveLockAsync(lockPath, cancellation.Token));
        }

        held.Dispose();
        using var acquired = await second.AcquireExclusiveLockAsync(lockPath, CancellationToken.None);
        Assert.NotNull(acquired);
    }

    [Fact]
    public void Workspace_operations_read_and_update_checkout_metadata()
    {
        var fs = new FileSystemWorkspaceFs();
        var directory = Path.Combine(_Root, "checkouts", "repo");
        fs.CreateDirectory(directory);
        var file = Path.Combine(directory, "data");
        File.WriteAllText(file, "abc");

        Assert.True(fs.DirectoryExists(directory));
        Assert.False(fs.DirectoryExists(Path.Combine(_Root, "missing")));
        Assert.Equal([directory], fs.EnumerateDirectories(Path.Combine(_Root, "checkouts")));
        Assert.Equal([file], fs.EnumerateFilesRecursive(directory));
        Assert.Contains(file, fs.EnumerateFileSystemEntries(directory));
        Assert.Equal(3, fs.GetFileLength(file));
        Assert.NotEqual(default, fs.GetCreationTimeUtc(directory));

        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fs.SetLastWriteTimeUtc(directory, timestamp);
        Assert.Equal(timestamp, fs.GetLastWriteTimeUtc(directory));

        var emptyDirectory = Path.Combine(_Root, "empty");
        fs.CreateDirectory(emptyDirectory);
        File.SetAttributes(emptyDirectory, File.GetAttributes(emptyDirectory) | FileAttributes.ReadOnly);
        fs.DeleteDirectory(emptyDirectory, recursive: false);
        Assert.False(Directory.Exists(emptyDirectory));
    }
}