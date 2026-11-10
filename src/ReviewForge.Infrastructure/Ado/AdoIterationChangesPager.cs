using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ReviewForge.Infrastructure.Ado;

internal static class AdoIterationChangesPager
{
    internal const int PageSize = 2000;

    internal static Task<IReadOnlyList<GitPullRequestChange>> FetchAsync(
        Func<int, int, CancellationToken, Task<GitPullRequestIterationChanges>> fetchPage,
        CancellationToken ct)
    {
        var skip = 0;
        return AdoPageAccumulator.FetchAllAsync(async pageCt =>
        {
            var page = await fetchPage(PageSize, skip, pageCt).ConfigureAwait(false);
            var entries = page.ChangeEntries.ToArray();
            skip += entries.Length;
            return new AdoPage<GitPullRequestChange>(entries, entries.Length == PageSize);
        }, ct);
    }
}
