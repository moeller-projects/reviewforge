using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class AdoInventoryReaderTests
{
    private static GitPullRequest Pr(int id) => new()
    {
        PullRequestId = id, Title = "draft", IsDraft = true,
        Repository = new GitRepository { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "repo" },
        LastMergeSourceCommit = new GitCommitRef { CommitId = "head" },
        SourceRefName = "refs/heads/feature", TargetRefName = "refs/heads/unwatched"
    };

    [Fact]
    public async Task Maps_cheap_inventory_work_item_references()
    {
        var pr = Pr(1);
        pr.WorkItemRefs = [new() { Id = "42" }];
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (_, _, _) => Task.FromResult(new AdoProjectPage([new() { Name = "project" }], null)),
            (_, skip, _, _) => Task.FromResult<IReadOnlyList<GitPullRequest>>(skip == 0 ? [pr] : []));
        Assert.Equal(1, Assert.Single((await reader.ReadAsync(TestContext.Current.CancellationToken)).Candidates).LinkedWorkItemCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_references_use_cheap_count_only_and_metadata_failure_does_not_make_inventory_partial(bool fail)
    {
        var reads = 0;
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (_, _, _) => Task.FromResult(new AdoProjectPage([new() { Name = "project" }], null)),
            (_, skip, _, _) => Task.FromResult<IReadOnlyList<GitPullRequest>>(skip == 0 ? [Pr(1)] : []),
            (_, _) => { reads++; return fail ? throw new Exception("secret") : Task.FromResult(0); });
        var inventory = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(inventory.Candidates);
        Assert.True(inventory.IsComplete);
        Assert.Equal(1, reads);
        Assert.Equal(fail ? null : (int?)0, row.LinkedWorkItemCount);
        Assert.Equal(fail ? 1 : 0, row.MetadataWarnings.Count);
        Assert.DoesNotContain("secret", string.Join(" ", row.MetadataWarnings));
    }

    [Fact]
    public async Task Browser_urls_remove_credentials_query_and_fragment()
    {
        var reader = new AdoInventoryReader("org", new Uri("https://user:secret@dev.azure.com/org/?token=secret#secret"),
            (token, top, ct) => Task.FromResult(new AdoProjectPage([new() {Name = "project"}], null)),
            (project, skip, top, ct) => Task.FromResult<IReadOnlyList<GitPullRequest>>(skip == 0 ? [Pr(1)] : []));
        var row = Assert.Single((await reader.ReadAsync(TestContext.Current.CancellationToken)).Candidates);
        Assert.Equal("https://dev.azure.com/org/project/_git/11111111-1111-1111-1111-111111111111/pullrequest/1", row.BrowserUrl);
        Assert.Throws<ArgumentException>(() => new AdoInventoryReader("org", new Uri("file:///secret"),
            (_, _, _) => throw new Exception(), (_, _, _, _) => throw new Exception()));
    }

    [Fact]
    public async Task Pages_until_empty_deduplicates_and_maps_without_filtering()
    {
        var projectTokens = new List<string?>();
        var prSkips = new List<int>();
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (token, top, ct) =>
            {
                projectTokens.Add(token);
                return Task.FromResult(new AdoProjectPage(
                    [new TeamProjectReference { Name = "project " + (token ?? "0") }], token is null ? "next" : null));
            },
            (project, skip, top, ct) =>
            {
                prSkips.Add(skip);
                return Task.FromResult<IReadOnlyList<GitPullRequest>>(skip == 0 ? [Pr(1)] : skip == 1 ? [Pr(1), Pr(2)] : []);
            });
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(result.IsComplete);
        Assert.Empty(result.SourceWarnings);
        Assert.Equal(new string?[] {null, "next"}, projectTokens);
        Assert.Equal(new[] {0, 1, 3, 0, 1, 3}, prSkips);
        Assert.Equal(4, result.Candidates.Count);
        var row = result.Candidates[0];
        Assert.True(row.Candidate.Pr.IsDraft);
        Assert.Equal("repo", row.RepositoryName);
        Assert.Contains("project%200/_git/11111111-1111-1111-1111-111111111111/pullrequest/1", row.BrowserUrl);
        Assert.Equal("head", row.Candidate.Pr.SourceCommitSha);
    }

    [Fact]
    public async Task Isolates_source_failures_and_invalid_identities_with_safe_warnings()
    {
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (token, top, ct) => Task.FromResult(new AdoProjectPage(
                [new() {Name = "bad"}, new() {Name = "good"}, new() {Name = ""}], null)),
            (project, skip, top, ct) => project == "bad" ? throw new InvalidOperationException("secret PAT")
                : Task.FromResult<IReadOnlyList<GitPullRequest>>(skip == 0 ? [Pr(1), new(), Pr(2)] : []));
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(3, result.SourceWarnings.Count);
        Assert.DoesNotContain("secret", string.Join(" ", result.SourceWarnings));
    }

    [Fact]
    public async Task Repeated_project_tokens_stop_with_warning_and_already_retrieved_rows_survive_page_failure()
    {
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (token, top, ct) => Task.FromResult(new AdoProjectPage([new() {Name = "project"}], "repeat")),
            (project, skip, top, ct) => skip == 0
                ? Task.FromResult<IReadOnlyList<GitPullRequest>>([Pr(1)]) : throw new Exception("secret"));
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsComplete);
        Assert.Single(result.Candidates);
        Assert.Equal(2, result.SourceWarnings.Count);

        var cancelled = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (token, top, ct) => Task.FromResult(new AdoProjectPage([new() {Name = "project"}], null)),
            (project, skip, top, ct) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Project_failure_is_incomplete_and_cancellation_is_not_a_warning()
    {
        var reader = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (skip, top, ct) => throw new Exception("secret"),
            (project, skip, top, ct) => throw new Exception());
        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsComplete);
        Assert.Single(result.SourceWarnings);
        Assert.Empty(result.Candidates);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cts.Token));
        var cancelled = new AdoInventoryReader("org", new Uri("https://dev.azure.com/org/"),
            (skip, top, ct) => throw new OperationCanceledException(),
            (project, skip, top, ct) => throw new Exception());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ReadAsync(TestContext.Current.CancellationToken));
    }
}
