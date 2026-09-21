using ReviewForge.Infrastructure.Git;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class LibGit2SharpGitOpsTests
{
    [Fact]
    public void MirrorPath_uses_repo_sibling_for_legacy_checkout()
    {
        var path = Path.Combine(Path.GetTempPath(), "work", "repo");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "work", "mirror", "repo"),
            LibGit2SharpGitOps.MirrorPath(path));
    }

    [Fact]
    public void MirrorPath_maps_per_head_checkout_to_repo_mirror()
    {
        var path = Path.Combine(Path.GetTempPath(), "work", "checkouts", "repo", "head-sha");

        Assert.Equal(Path.Combine(Path.GetTempPath(), "work", "mirror", "repo"),
            LibGit2SharpGitOps.MirrorPath(path));
    }
}
