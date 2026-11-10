namespace ReviewForge.Infrastructure.Ado;

internal readonly record struct AdoPage<TItem>(IReadOnlyList<TItem> Items, bool HasMore);

internal static class AdoPageAccumulator
{
    internal static async Task<IReadOnlyList<TItem>> FetchAllAsync<TItem>(
        Func<CancellationToken, Task<AdoPage<TItem>>> fetchPage,
        CancellationToken ct)
    {
        var items = new List<TItem>();
        while (true)
        {
            var page = await fetchPage(ct).ConfigureAwait(false);
            items.AddRange(page.Items);
            if (!page.HasMore)
            {
                return items;
            }
        }
    }
}
