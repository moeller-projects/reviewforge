using LibGit2Sharp;
using ReviewForge.Core.Analysis;
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
            await Assert.ThrowsAnyAsync<LibGit2SharpException>(() => git.PushAsync(stalePath, remotePath, branch, committedSha, null, CancellationToken.None));

            var changedTip = await Assert.ThrowsAsync<PrHeadChangedException>(() => git.PushAsync(stalePath, remotePath, branch, initialSha, null, CancellationToken.None));
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

    [Fact]
    public async Task Pull_request_diff_uses_merge_base_and_excludes_target_only_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-git-merge-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repoPath = Path.Combine(root, "repo");
            Repository.Init(repoPath);
            string mergeBaseSha, targetSha, sourceSha;
            var targetOnlyPaths = new[]
            {
                "modules/laekkerai.branches/src/Laekkerai.Branches.Application/DistributedEvents/BranchUpdatedEventHandler.cs",
                "modules/laekkerai.branches/src/Laekkerai.Branches.Domain/Branches/Branch.cs",
                "modules/laekkerai.branches/test/Laekkerai.Branches.Application.Tests/Branches/BranchesAppServiceTests.cs",
            };
            var prPaths = new[]
            {
                "apps/shop/src/app/delivery-details/shared/window-picker/fixed-window-picker/fixed-window-picker.component.spec.ts",
                "apps/shop/src/app/delivery-details/shared/window-picker/fixed-window-picker/fixed-window-picker.component.ts",
                "apps/shop/src/app/delivery-details/shared/window-picker/window-picker.component.html",
                "openspec/changes/fix-auto-select-fixed-delivery-window-12359/proposal.md",
                "openspec/changes/fix-auto-select-fixed-delivery-window-12359/specs/ordering-lieferinformationen-im-checkout/spec.md",
                "openspec/changes/fix-auto-select-fixed-delivery-window-12359/tasks.md",
            };

            using (var repo = new Repository(repoPath))
            {
                var identity = new Signature("author", "author@example.com", DateTimeOffset.UtcNow);
                foreach (var path in targetOnlyPaths)
                    WriteFile(path, "common version\n");
                Commands.Stage(repo, "*");
                mergeBaseSha = repo.Commit("common base", identity, identity).Sha;

                var target = repo.CreateBranch("target", repo.Lookup<Commit>(mergeBaseSha));
                Commands.Checkout(repo, target);
                foreach (var path in targetOnlyPaths)
                    WriteFile(path, "target branch version\n");
                Commands.Stage(repo, "*");
                targetSha = repo.Commit("target-only changes", identity, identity).Sha;

                var source = repo.CreateBranch("source", repo.Lookup<Commit>(mergeBaseSha));
                Commands.Checkout(repo, source);
                foreach (var path in prPaths)
                    WriteFile(path, "PR change\n");
                Commands.Stage(repo, "*");
                sourceSha = repo.Commit("PR changes", identity, identity).Sha;
            }

            using var scheduler = new GitOperationScheduler(1);
            var git = new LibGit2SharpGitOps(scheduler: scheduler);
            var actualMergeBase = await git.GetMergeBaseShaAsync(
                repoPath, targetSha, sourceSha, CancellationToken.None);
            Assert.Equal(mergeBaseSha, actualMergeBase);

            var correctDiff = await git.GetDiffAsync(repoPath, actualMergeBase, sourceSha, CancellationToken.None);
            var oldDiff = await git.GetDiffAsync(repoPath, targetSha, sourceSha, CancellationToken.None);
            var correctFiles = DiffIndex.Parse(correctDiff).Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldFiles = DiffIndex.Parse(oldDiff).Files.ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.Equal(prPaths.Length, correctFiles.Count);
            Assert.Equal(prPaths.ToHashSet(StringComparer.OrdinalIgnoreCase), correctFiles);
            Assert.Equal(prPaths.Length + targetOnlyPaths.Length, oldFiles.Count);
            Assert.True(targetOnlyPaths.All(oldFiles.Contains));

            void WriteFile(string relativePath, string contents)
            {
                var path = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, contents);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Push_rejects_a_force_reset_to_an_ancestor_of_the_pinned_head()
    {
        var root = Path.Combine(Path.GetTempPath(), "reviewforge-git-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var remotePath = Path.Combine(root, "remote.git");
            Repository.Init(remotePath, isBare: true);
            var seedPath = Path.Combine(root, "seed");
            Repository.Init(seedPath);
            string rootSha, pinnedSha, branch;
            using (var seed = new Repository(seedPath))
            {
                var identity = new Signature("author", "author@example.com", DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(seedPath, "file.txt"), "one\n");
                Commands.Stage(seed, "file.txt");
                rootSha = seed.Commit("root", identity, identity).Sha;
                File.WriteAllText(Path.Combine(seedPath, "file.txt"), "two\n");
                Commands.Stage(seed, "file.txt");
                pinnedSha = seed.Commit("pinned", identity, identity).Sha;
                branch = seed.Head.FriendlyName;
                var remote = seed.Network.Remotes.Add("origin", remotePath);
                seed.Network.Push(remote, $"refs/heads/{branch}:refs/heads/{branch}");
            }

            using var scheduler = new GitOperationScheduler(1);
            var git = new LibGit2SharpGitOps(scheduler: scheduler);
            var checkoutPath = Path.Combine(root, "checkout");
            var mirrorPath = Path.Combine(root, "mirror.git");
            await git.CloneOrOpenAsync(remotePath, checkoutPath, null, CancellationToken.None, mirrorPath);
            await git.CheckoutAsync(checkoutPath, pinnedSha, CancellationToken.None);
            File.WriteAllText(Path.Combine(checkoutPath, "file.txt"), "fixed\n");
            await git.CommitAsync(
                checkoutPath, "fix: update file", "ReviewForge", "bot@example.com", ["file.txt"], CancellationToken.None);

            // The author force-resets the branch to an ANCESTOR of the pinned head after the
            // pre-read. A plain non-force push would still fast-forward over the reset; the
            // expected-tip CAS must reject it instead.
            using (var seed = new Repository(seedPath))
            {
                seed.Refs.Add("refs/heads/reset-target", rootSha, allowOverwrite: true);
                var remote = seed.Network.Remotes["origin"];
                seed.Network.Push(remote, $"+refs/heads/reset-target:refs/heads/{branch}");
            }

            var ex = await Assert.ThrowsAsync<PrHeadChangedException>(() => git.PushAsync(checkoutPath, remotePath, branch, pinnedSha, null, CancellationToken.None));
            Assert.Equal(pinnedSha, ex.Expected);
            Assert.Equal(rootSha, ex.Actual);
            Assert.Equal(rootSha,
                await git.GetRemoteTipAsync(checkoutPath, remotePath, branch, null, CancellationToken.None));
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