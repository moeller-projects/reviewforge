using ReviewForge.Core.Workspaces;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class CheckoutEvictionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2025, 1, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Startup_recovery_selects_all_private_paths_for_lock_checked_cleanup()
    {
        string[] paths = ["private/a", "private/b"];

        var selected = CheckoutEvictionPolicy.SelectStartupRecoveryCandidates(paths);

        Assert.Same(paths, selected);
    }

    [Fact]
    public void Private_sweep_selects_only_checkouts_strictly_older_than_the_age_limit()
    {
        var options = new CheckoutEvictionOptions {PrivateMaxAgeMinutes = 60};
        PrivateCheckoutMetadata[] checkouts =
        [
            new("private/exact", Now.UtcDateTime.AddMinutes(-60), Now),
            new("private/old", Now.UtcDateTime.AddMinutes(-61), Now),
        ];

        var selected = CheckoutEvictionPolicy.SelectPrivateCandidates(checkouts, options);

        Assert.Equal(["private/old"], selected.Select(checkout => checkout.Path));
    }

    [Fact]
    public void Pooled_sweep_selects_stale_and_over_capacity_heads_in_input_recency_order()
    {
        var options = new CheckoutEvictionOptions
        {
            MaxAge = TimeSpan.FromDays(3),
            MaxCheckoutsPerRepo = 2,
        };
        PooledCheckoutMetadata[] newestFirst =
        [
            new("checkouts/r/newest", "r", "newest", Now.UtcDateTime.AddHours(-1)),
            new("checkouts/r/cutoff", "r", "cutoff", Now.UtcDateTime.AddDays(-3)),
            new("checkouts/r/over-capacity", "r", "over-capacity", Now.UtcDateTime.AddMinutes(-30)),
            new("checkouts/r/old", "r", "old", Now.UtcDateTime.AddDays(-4)),
        ];

        var selected = CheckoutEvictionPolicy.SelectPooledCandidates(newestFirst, options, Now);

        Assert.Equal(["checkouts/r/over-capacity", "checkouts/r/old"], selected.Select(checkout => checkout.Path));
    }

    [Fact]
    public void Global_budget_orders_lru_candidates_and_excludes_prior_delete_failures()
    {
        var options = new CheckoutEvictionOptions {MaxTotalBytes = 11};
        PooledCheckoutMetadata[] survivors =
        [
            new("checkouts/r/recent", "r", "recent", Now.UtcDateTime.AddHours(-1), Size: 6),
            new("checkouts/r/failed-old", "r", "failed-old", Now.UtcDateTime.AddDays(-3), Size: 6, DeletionFailed: true),
            new("checkouts/r/old", "r", "old", Now.UtcDateTime.AddDays(-4), Size: 6),
        ];

        var (totalBytes, candidates) = CheckoutEvictionPolicy.SelectBudgetCandidates(survivors, options);

        Assert.Equal(18, totalBytes);
        Assert.Equal(["checkouts/r/old", "checkouts/r/recent"], candidates.Select(checkout => checkout.Path));
    }

    [Fact]
    public void Global_budget_selects_nothing_when_disabled_or_already_satisfied()
    {
        PooledCheckoutMetadata[] survivors =
        [new("checkouts/r/one", "r", "one", Now.UtcDateTime, Size: 5)];

        var disabled = CheckoutEvictionPolicy.SelectBudgetCandidates(
            survivors, new CheckoutEvictionOptions {MaxTotalBytes = 0});
        var withinBudget = CheckoutEvictionPolicy.SelectBudgetCandidates(
            survivors, new CheckoutEvictionOptions {MaxTotalBytes = 5});

        Assert.Equal(5, disabled.TotalBytes);
        Assert.Empty(disabled.Candidates);
        Assert.Equal(5, withinBudget.TotalBytes);
        Assert.Empty(withinBudget.Candidates);
    }
}
