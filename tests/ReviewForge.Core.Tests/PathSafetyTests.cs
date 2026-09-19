using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public class PathSafetyTests : IDisposable
{
    private readonly string _Root;
    private readonly string _Sibling;

    public PathSafetyTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-pathsafety-" + Guid.NewGuid().ToString("N"));
        _Sibling = _Root + "-evil";
        Directory.CreateDirectory(Path.Combine(_Root, "sub"));
        Directory.CreateDirectory(_Sibling);
    }

    public void Dispose()
    {
        Directory.Delete(_Root, recursive: true);
        Directory.Delete(_Sibling, recursive: true);
    }

    [Fact]
    public void Child_and_root_are_contained()
    {
        Assert.True(PathSafety.IsContained(_Root, Path.Combine(_Root, "sub", "file.cs")));
        Assert.True(PathSafety.IsContained(_Root, _Root));
    }

    [Fact]
    public void Sibling_with_shared_prefix_is_not_contained()
        => Assert.False(PathSafety.IsContained(_Root, Path.Combine(_Sibling, "file.cs")));

    [Fact]
    public void Parent_escape_is_not_contained()
        => Assert.False(PathSafety.IsContained(_Root, Path.Combine(_Root, "..", "outside.txt")));

    [Fact]
    public void Unrelated_absolute_path_is_not_contained()
        => Assert.False(PathSafety.IsContained(_Root, Path.GetTempPath()));
}
