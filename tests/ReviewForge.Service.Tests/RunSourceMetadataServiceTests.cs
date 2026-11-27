using ReviewForge.Core.Domain;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunSourceMetadataServiceTests
{
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    [Fact]
    public async Task Metadata_uses_full_pr_identity_and_keeps_current_head_separate_from_reviewed_head()
    {
        var source = new FakePullRequestSource();
        source.Inventory = new([new(new(Key, source.Pr with { SourceCommitSha = "current" }, "main", "creator", "Creator"),
            "Repository", "https://example.com/pr")], true, []);
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var service = new RunSourceMetadataService(cache);

        var metadata = await service.GetAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal("current", metadata.CurrentHeadSha);
        Assert.Equal("Repository", metadata.RepositoryName);
        Assert.NotNull(metadata.ObservedAt);
        Assert.Null(metadata.Warning);
        var absent = await service.GetAsync(Key with { RepositoryId = "another" }, TestContext.Current.CancellationToken);
        Assert.Null(absent.CurrentHeadSha);
        Assert.NotNull(absent.Warning);
    }

    [Fact]
    public async Task Unavailable_source_has_a_safe_explicit_warning_without_hiding_durable_run_details()
    {
        var source = new FakePullRequestSource { ThrowOnGetInventory = new InvalidOperationException("private provider message") };
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var metadata = await new RunSourceMetadataService(cache).GetAsync(Key, TestContext.Current.CancellationToken);

        Assert.Null(metadata.CurrentHeadSha);
        Assert.NotNull(metadata.Warning);
        Assert.DoesNotContain("private", metadata.Warning);
        Assert.True(metadata.IsStale);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_into_metadata_unavailability()
    {
        using var cache = new DashboardInventoryCache(new FakePullRequestSource(), TimeProvider.System);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RunSourceMetadataService(cache).GetAsync(Key, cancelled.Token));
    }
}
