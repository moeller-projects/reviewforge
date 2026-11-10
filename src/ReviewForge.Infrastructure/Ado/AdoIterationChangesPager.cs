using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace ReviewForge.Infrastructure.Ado;

internal static class AdoIterationChangesPager
{
    internal const int PageSize = 2000;

    internal static async Task<IReadOnlyList<GitPullRequestChange>> FetchAsync(
        Func<int, int, CancellationToken, Task<GitPullRequestIterationChanges>> fetchPage,
        CancellationToken ct)
    {
        var changes = new List<GitPullRequestChange>();
        var skip = 0;

        while (true)
        {
            var page = await fetchPage(PageSize, skip, ct).ConfigureAwait(false);
            var entries = page.ChangeEntries.ToArray();
            changes.AddRange(entries);

            if (entries.Length < PageSize)
            {
                return changes;
            }

            skip += entries.Length;
        }
    }
}
