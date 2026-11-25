using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class DashboardEligibilityTests
{
    [Theory]
    [InlineData(true, "main", "creator", 1, "Ineligible", 0)]
    [InlineData(false, "other", "creator", 1, "Ineligible", 0)]
    [InlineData(false, "main", "other", 1, "Ineligible", 0)]
    [InlineData(false, "main", "creator", 0, "Ineligible", 0)]
    [InlineData(false, "main", "creator", null, "Unknown", 1)]
    [InlineData(false, "main", "creator", 1, "Eligible", 1)]
    [InlineData(false, "", "creator", 1, "Unknown", 1)]
    [InlineData(false, "main", "", 1, "Unknown", 1)]
    [InlineData(false, "", "other", 1, "Ineligible", 0)]
    [InlineData(false, "", "", 0, "Ineligible", 0)]
    public async Task Basic_rules_and_attention(bool draft, string branch, string creator, int? links, string eligibility, int attention)
    {
        var source = new FakePullRequestSource();
        var key = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(key, source.Pr with { IsDraft = draft }, branch, creator, creator), "Repo", "https://example.com", links)], false, ["Partial inventory."]);
        var queue = new ReviewQueue();
        var service = new DashboardQueryService(new(source, TimeProvider.System), new(), queue, new(), new FakeFindingStore(), TimeProvider.System,
            new DiscoveryOptions { Creators = ["creator"] });
        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(response.Rows);
        Assert.Equal(eligibility, row.Eligibility!.Status.ToString());
        Assert.NotEmpty(row.Eligibility.Reason);
        Assert.Equal(attention, response.Summary!.NeedsAttention);
        Assert.Equal(1, response.Summary.OpenPullRequests);
        Assert.True(response.Summary.IsPartial);
        Assert.False(response.DiscoveryEnabled);
        Assert.Contains("overlap", response.Summary.HelpText);
        Assert.Empty(await queue.GetPendingAsync(new HashSet<PrKey> { key }, TestContext.Current.CancellationToken));
        Assert.Equal(0, source.ThreadFetches);
        Assert.Equal(0, source.WorkItemFetches);
    }

    [Theory]
    [InlineData(RunState.Queued, 1, 0)]
    [InlineData(RunState.Running, 1, 0)]
    [InlineData(RunState.Completed, 0, 0)]
    [InlineData(RunState.Skipped, 0, 0)]
    [InlineData(RunState.Failed, 0, 1)]
    public async Task Held_claim_blocks_reruns_without_counting_terminal_reviews_as_reviewing(
        RunState state, int reviewing, int attention)
    {
        var source = new FakePullRequestSource();
        var key = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(key, source.Pr, "unwatched", "creator", "Creator"), "Repo", "https://example.com", 1)], true, []);
        var tracker = new RunTracker();
        var claims = new InFlightClaims();
        var request = new ReviewRequest(Guid.NewGuid(), key, DateTimeOffset.UtcNow, HeadSha: source.Pr.SourceCommitSha);
        Assert.True(claims.TryClaim(key, request.RunId, out _));
        tracker.Register(request);
        tracker.Set(request.RunId, key, state);
        using var inventory = new DashboardInventoryCache(source, TimeProvider.System);
        var service = new DashboardQueryService(inventory, tracker, new ReviewQueue(), claims, new FakeFindingStore(), TimeProvider.System);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(response.Rows);
        Assert.Equal(state, row.ActiveBlocker!.State);
        Assert.False(row.CanRerun);
        Assert.Equal(reviewing, response.Summary!.Reviewing);
        Assert.Equal(attention, response.Summary.NeedsAttention);
        Assert.Equal(state == RunState.Completed ? 1 : 0, response.Summary.Reviewed);
    }

    [Fact]
    public async Task Missing_head_is_unknown_and_keeps_unreviewed_pr_in_attention()
    {
        var source = new FakePullRequestSource();
        var key = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(key, source.Pr with { SourceCommitSha = "" }, "main", "creator", "Creator"),
            "Repo", "https://example.com", 1)], true, []);
        var service = new DashboardQueryService(new(source, TimeProvider.System), new(), new ReviewQueue(), new(),
            new FakeFindingStore(), TimeProvider.System, new DiscoveryOptions());
        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BasicEligibilityStatus.Unknown, Assert.Single(response.Rows).Eligibility!.Status);
        Assert.Equal(1, response.Summary!.NeedsAttention);
    }

    [Theory]
    [InlineData("old-head", 0)]
    [InlineData("", 1)]
    public async Task Failure_attention_preserves_head_attribution_but_includes_unknown_head_reruns(string failedHead, int expected)
    {
        var source = new FakePullRequestSource();
        var key = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(key, source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com", 1)], true, []);
        var store = new FakeFindingStore();
        var now = DateTimeOffset.UtcNow;
        store.Runs.Add(new(Guid.NewGuid(), key, "head-sha", ReviewKind.Full, now.AddMinutes(-10), now.AddMinutes(-9), true, []));
        store.Runs.Add(new(Guid.NewGuid(), key, failedHead, ReviewKind.Full, now.AddMinutes(-5), now.AddMinutes(-4), false, []));
        var service = new DashboardQueryService(new(source, TimeProvider.System), new(), new ReviewQueue(), new(), store, TimeProvider.System);
        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(response.Rows).CurrentHeadReviewed);
        Assert.Equal(RunState.Failed, response.Rows[0].LatestPrAttempt!.State);
        Assert.Equal(expected, response.Summary!.NeedsAttention);
    }

    [Theory]
    [InlineData(false, false, null, RunKind.Review, 1, 0)]
    [InlineData(true, false, null, RunKind.Review, 0, 0)]
    [InlineData(true, true, null, RunKind.Review, 1, 0)]
    [InlineData(true, true, "old", RunKind.Review, 0, 1)]
    [InlineData(true, true, null, RunKind.Review, 0, 1)]
    [InlineData(true, true, "head-sha", RunKind.Resolve, 1, 0)]
    public async Task Reviewed_failure_and_pr_wide_active_review_predicates(bool reviewed, bool failed, string? activeHead, RunKind kind, int attention, int reviewing)
    {
        var source = new FakePullRequestSource();
        var key = new PrKey("org", "project", "repo", 1);
        source.Inventory = new([new(new(key, source.Pr, failed ? "unwatched" : "main", "creator", "Creator"), "Repo", "https://example.com", 1)], true, []);
        var store = new FakeFindingStore();
        var now = DateTimeOffset.UtcNow;
        if (reviewed) store.Runs.Add(new(Guid.NewGuid(), key, "head-sha", ReviewKind.Full, now.AddMinutes(-10), now.AddMinutes(-9), true, [new("finding", "rule", "high", "title", null, null, null)]));
        if (failed) store.Runs.Add(new(Guid.NewGuid(), key, "head-sha", ReviewKind.Full, now.AddMinutes(-5), now.AddMinutes(-4), false, []));
        var queue = new ReviewQueue();
        if (reviewing == 1 || kind == RunKind.Resolve) queue.TryEnqueue(new(Guid.NewGuid(), key, now, HeadSha: activeHead, Kind: kind));
        var service = new DashboardQueryService(new(source, TimeProvider.System), new(), queue, new(), store, TimeProvider.System,
            new DiscoveryOptions { SweepInterval = TimeSpan.FromMinutes(1) });
        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(attention, response.Summary!.NeedsAttention);
        Assert.Equal(reviewing, response.Summary.Reviewing);
        Assert.Equal(reviewed ? 1 : 0, response.Summary.Reviewed);
        Assert.True(response.DiscoveryEnabled);
        if (reviewed && !failed)
        {
            Assert.Equal("Unknown", response.Rows[0].Eligibility!.Status.ToString());
            Assert.Contains("human-comment", response.Rows[0].Eligibility!.Reason);
        }
    }
}
