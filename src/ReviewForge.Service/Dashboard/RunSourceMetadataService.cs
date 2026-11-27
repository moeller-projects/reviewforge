using ReviewForge.Core.Domain;

namespace ReviewForge.Service;

/// <summary>Latest observed source metadata, not a publication-time head guarantee. Missing
/// closed/inaccessible PR metadata must not prevent inspection of durable run history.</summary>
public sealed record RunSourceMetadata(string? Title, string? RepositoryName, string? BrowserUrl,
    string? CurrentHeadSha, DateTimeOffset? ObservedAt, bool IsStale, string? Warning);

public sealed class RunSourceMetadataService(DashboardInventoryCache inventory)
{
    public async Task<RunSourceMetadata> GetAsync(PrKey pr, CancellationToken ct)
    {
        CachedInventory cached;
        try { cached = await inventory.GetAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ObjectDisposedException)
        {
            // Optional metadata failure is visible; never substitute another source/provider.
            return new(null, null, null, null, null, true, "Current PR metadata is unavailable. Publication will check the head again.");
        }
        var item = cached.Inventory.Candidates.FirstOrDefault(i => i.Candidate.Key == pr);
        if (item is null)
            return new(null, null, null, null, cached.FetchedAt, cached.IsStale,
                "PR is not in the retrieved inventory. It may be closed or unavailable; the current head is unknown.");
        return new(item.Candidate.Pr.Title, item.RepositoryName, item.BrowserUrl,
            item.Candidate.Pr.SourceCommitSha, cached.FetchedAt, cached.IsStale,
            cached.RefreshFailed ? "Source refresh failed; showing the last observed head."
                : cached.IsStale ? "Source metadata is stale. Publication will check the head again." : null);
    }
}
