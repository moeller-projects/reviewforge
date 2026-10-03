using LibGit2Sharp;
using ReviewForge.Core.Pipeline;

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

    [Fact]
    public async Task Commit_push_and_remote_tip_use_the_authoritative_bare_remote()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-git-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var remotePath = Path.Combine(root, "remote.git");
            Repository.Init(remotePath, isBare: true);
            var seedPath = Path.Combine(root, "seed");
            Repository.Init(seedPath);
            string initialSha;
            string branch;
            using (var seed = new Repository(seedPath))
            {
                File.WriteAllText(Path.Combine(seedPath, "file.txt"), "before\n");
                Commands.Stage(seed, "file.txt");
                var identity = new Signature("author", "author@example.com", DateTimeOffset.UtcNow);
                var initial = seed.Commit("seed", identity, identity);
                initialSha = initial.Sha;
                branch = seed.Head.FriendlyName;
                var remote = seed.Network.Remotes.Add("origin", remotePath);
                seed.Network.Push(remote, $"refs/heads/{branch}:refs/heads/{branch}");
            }

            using var scheduler = new GitOperationScheduler(1);
            var git = new LibGit2SharpGitOps(scheduler: scheduler);
            var checkoutPath = Path.Combine(root, "checkout");
            var mirrorPath = Path.Combine(root, "mirror.git");
            await git.CloneOrOpenAsync(remotePath, checkoutPath, null, CancellationToken.None, mirrorPath);
            await git.EnsureCommitsAsync(checkoutPath, remotePath, initialSha, initialSha, null, CancellationToken.None);
            await git.CheckoutAsync(checkoutPath, initialSha, CancellationToken.None);
            Assert.Equal(initialSha,
                await git.GetRemoteTipAsync(checkoutPath, remotePath, branch, null, CancellationToken.None));

            var stalePath = Path.Combine(root, "stale");
            await git.CloneOrOpenAsync(remotePath, stalePath, null, CancellationToken.None, mirrorPath);
            await git.CheckoutAsync(stalePath, initialSha, CancellationToken.None);

            File.WriteAllText(Path.Combine(checkoutPath, "file.txt"), "after\n");
            var committedSha = await git.CommitAsync(
                checkoutPath, "fix: update file", "ReviewForge", "bot@example.com", ["file.txt"], CancellationToken.None);
            await git.PushAsync(checkoutPath, remotePath, branch, initialSha, null, CancellationToken.None);

            Assert.Equal(committedSha,
                await git.GetRemoteTipAsync(checkoutPath, remotePath, branch, null, CancellationToken.None));

            File.WriteAllText(Path.Combine(stalePath, "file.txt"), "divergent\n");
            await git.CommitAsync(
                stalePath, "fix: divergent update", "ReviewForge", "bot@example.com", ["file.txt"], CancellationToken.None);
            await Assert.ThrowsAnyAsync<LibGit2SharpException>(
                () => git.PushAsync(stalePath, remotePath, branch, committedSha, null, CancellationToken.None));

            var changedTip = await Assert.ThrowsAsync<PrHeadChangedException>(
                () => git.PushAsync(stalePath, remotePath, branch, initialSha, null, CancellationToken.None));
            Assert.Equal(initialSha, changedTip.Expected);
            Assert.Equal(committedSha, changedTip.Actual);

        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
    }
}
