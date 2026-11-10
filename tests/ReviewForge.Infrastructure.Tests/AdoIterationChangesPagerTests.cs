using Microsoft.TeamFoundation.SourceControl.WebApi;
using ReviewForge.Infrastructure.Ado;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class AdoIterationChangesPagerTests
{
    [Fact]
    public async Task Fetches_another_page_when_the_page_is_full()
    {
        var firstPage = Enumerable.Range(0, AdoIterationChangesPager.PageSize)
            .Select(_ => new GitPullRequestChange())
            .ToArray();
        var secondPage = new[] {new GitPullRequestChange()};
        var calls = new List<(int Top, int Skip)>();

        var result = await AdoIterationChangesPager.FetchAsync((top, skip, _) =>
        {
            calls.Add((top, skip));
            var entries = skip == 0 ? firstPage : secondPage;
            return Task.FromResult(new GitPullRequestIterationChanges {ChangeEntries = entries});
        }, CancellationToken.None);

        Assert.Equal(AdoIterationChangesPager.PageSize + 1, result.Count);
        Assert.Equal(new[] {(AdoIterationChangesPager.PageSize, 0), (AdoIterationChangesPager.PageSize, AdoIterationChangesPager.PageSize)}, calls);
        Assert.Same(firstPage[0], result[0]);
        Assert.Same(secondPage[0], result[^1]);
    }

    [Fact]
    public async Task Stops_when_the_page_is_shorter_than_the_requested_size()
    {
        var entries = new[] {new GitPullRequestChange()};
        var calls = new List<(int Top, int Skip)>();

        var result = await AdoIterationChangesPager.FetchAsync((top, skip, _) =>
        {
            calls.Add((top, skip));
            return Task.FromResult(new GitPullRequestIterationChanges {ChangeEntries = entries});
        }, CancellationToken.None);

        Assert.Same(entries[0], Assert.Single(result));
        Assert.Equal(new[] {(AdoIterationChangesPager.PageSize, 0)}, calls);
    }
}
