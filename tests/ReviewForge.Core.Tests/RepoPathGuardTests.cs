using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

/// <summary>RepoPathGuard: containment, deny list, and symlink behavior extracted from RepoReadTools.</summary>
public sealed class RepoPathGuardTests : IDisposable
{
    private readonly string _Root;

    public RepoPathGuardTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_Root, "src"));
        File.WriteAllText(Path.Combine(_Root, "src", "A.cs"), "class A {}");
        File.WriteAllText(Path.Combine(_Root, ".env"), "SECRET=1");
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private RepoPathGuard Guard(IEnumerable<string>? deny = null) => new(_Root, deny);

    [Fact]
    public void Resolve_normalizes_root_and_resolves_contained_path()
    {
        var g = Guard();
        var resolved = g.Resolve("src/A.cs", out var error);
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath(Path.Combine(_Root, "src", "A.cs")), resolved);
    }

    [Fact]
    public void Resolve_denies_secrets_by_default()
    {
        var g = Guard();
        Assert.Null(g.Resolve(".env", out var error));
        Assert.Equal("access denied: .env", error);
    }

    [Fact]
    public void Resolve_denies_path_traversal_escape()
    {
        var g = Guard();
        Assert.Null(g.Resolve("../../etc/passwd", out var error));
        Assert.Equal("access denied: path escapes repository root", error);
    }

    [Fact]
    public void Resolve_allows_empty_path_as_root()
    {
        var g = Guard();
        Assert.Equal(g.Root, g.Resolve(null, out var error));
        Assert.Null(error);
        Assert.Equal(g.Root, g.Resolve("", out error));
        Assert.Null(error);
    }

    [Fact]
    public void Resolve_honors_custom_deny_patterns()
    {
        var g = Guard([@"\.cs$"]);
        Assert.Null(g.Resolve("src/A.cs", out var error));
        Assert.Equal("access denied: src/A.cs", error);
        // Custom patterns replace the defaults entirely.
        Assert.NotNull(g.Resolve(".env", out _));
    }

    [Fact]
    public void Resolve_refuses_symlink_inside_checkout()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // symlink creation requires elevation on Windows; covered on Linux CI
        }

        var link = Path.Combine(_Root, "src", "link.cs");
        File.CreateSymbolicLink(link, Path.Combine(_Root, "src", "A.cs"));
        var g = Guard();
        Assert.Null(g.Resolve("src/link.cs", out var error));
        Assert.Equal("access denied: src/link.cs", error);
    }

    [Fact]
    public void IsDenied_matches_case_insensitively()
    {
        var g = Guard();
        Assert.True(g.IsDenied("SRC/CERT.PEM"));
        Assert.False(g.IsDenied("src/A.cs"));
    }

    [Fact]
    public void IsResolvedDenied_reports_escape()
    {
        var g = Guard();
        var (rootReal, rootLinks) = g.ResolveRoot();
        Assert.True(g.IsResolvedDenied(Path.Combine(_Root, "..", "outside.txt"), rootReal, rootLinks));
        Assert.False(g.IsResolvedDenied(Path.Combine(_Root, "src", "A.cs"), rootReal, rootLinks));
    }

    [Fact]
    public void ResolveRoot_returns_stable_real_root()
    {
        var g = Guard();
        var (rootReal, _) = g.ResolveRoot();
        Assert.Equal(Path.GetFullPath(_Root), rootReal);
    }
}
