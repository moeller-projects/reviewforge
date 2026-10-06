namespace ReviewForge.Core.Workspaces;

internal sealed record PrivateCheckoutMetadata(string Path, DateTime CreatedAtUtc, DateTimeOffset EvaluatedAt);

internal sealed record PooledCheckoutMetadata(
    string Path,
    string RepositoryId,
    string Head,
    DateTime LastWriteUtc,
    long Size = 0,
    bool DeletionFailed = false);

/// <summary>Pure checkout retention decisions, separate from lock acquisition and deletion.</summary>
internal static class CheckoutEvictionPolicy
{
    public static IReadOnlyList<string> SelectStartupRecoveryCandidates(IReadOnlyList<string> privatePaths)
        => privatePaths;

    public static IEnumerable<PrivateCheckoutMetadata> SelectPrivateCandidates(
        IEnumerable<PrivateCheckoutMetadata> checkouts,
        CheckoutEvictionOptions options)
    {
        var maxAge = TimeSpan.FromMinutes(options.PrivateMaxAgeMinutes);
        foreach (var checkout in checkouts)
        {
            if (checkout.EvaluatedAt.UtcDateTime - checkout.CreatedAtUtc > maxAge)
            {
                yield return checkout;
            }
        }
    }

    /// <summary>Input must be ordered newest first, matching filesystem sweep ordering.</summary>
    public static IReadOnlyList<PooledCheckoutMetadata> SelectPooledCandidates(
        IReadOnlyList<PooledCheckoutMetadata> checkouts,
        CheckoutEvictionOptions options,
        DateTimeOffset now)
    {
        var cutoff = now - options.MaxAge;
        var candidates = new List<PooledCheckoutMetadata>();
        for (var i = 0; i < checkouts.Count; i++)
        {
            var checkout = checkouts[i];
            if (i >= options.MaxCheckoutsPerRepo || checkout.LastWriteUtc < cutoff.UtcDateTime)
            {
                candidates.Add(checkout);
            }
        }

        return candidates;
    }

    public static (long TotalBytes, IReadOnlyList<PooledCheckoutMetadata> Candidates) SelectBudgetCandidates(
        IReadOnlyList<PooledCheckoutMetadata> survivors,
        CheckoutEvictionOptions options)
    {
        var totalBytes = survivors.Sum(checkout => checkout.Size);
        if (options.MaxTotalBytes <= 0 || totalBytes <= options.MaxTotalBytes)
        {
            return (totalBytes, []);
        }

        return (
            totalBytes,
            survivors.Where(checkout => !checkout.DeletionFailed)
                .OrderBy(checkout => checkout.LastWriteUtc)
                .ToArray());
    }
}
