using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class DashboardEndpointTests
{
    [Fact]
    public async Task Dashboard_is_protected_cached_no_store_and_has_no_discovery_side_effects()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        factory.Source.Inventory = new([new(new(new("org", "project", "repo", 1), factory.Source.Pr,
            "main", "alice", "Alice"), "Repository", "https://dev.azure.com/org/project/_git/repo/pullrequest/1")],
            false, ["Source unavailable."]);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var response = await client.GetAsync("/reviews/dashboard", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.False(json.RootElement.GetProperty("isComplete").GetBoolean());
        var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("Repository", row.GetProperty("pullRequest").GetProperty("repositoryName").GetString());
        Assert.False(row.GetProperty("currentHeadReviewed").GetBoolean());
        Assert.True(row.GetProperty("canRerun").GetBoolean());
        Assert.Equal((int)BasicEligibilityStatus.Unknown, row.GetProperty("eligibility").GetProperty("status").GetInt32());
        Assert.Contains("unknown", row.GetProperty("eligibility").GetProperty("reason").GetString());
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("openPullRequests").GetInt32());
        Assert.Equal(1, summary.GetProperty("needsAttention").GetInt32());
        Assert.True(summary.GetProperty("isPartial").GetBoolean());
        Assert.Equal(factory.Services.GetRequiredService<DiscoveryOptions>().SweepInterval is not null,
            json.RootElement.GetProperty("discoveryEnabled").GetBoolean());
        Assert.Single(json.RootElement.GetProperty("sourceWarnings").EnumerateArray());
        Assert.True(json.RootElement.TryGetProperty("inventoryFetchedAt", out _));
        Assert.True(json.RootElement.TryGetProperty("generatedAt", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/dashboard", ct)).StatusCode);
        Assert.Equal(1, factory.Source.InventoryFetches);
        Assert.Equal(0, factory.Source.OpenPullRequestsFetches);
        Assert.Equal(0, factory.Source.WorkItemFetches);
        Assert.Equal(0, factory.Source.ThreadFetches);
        Assert.Equal(0, factory.Services.GetRequiredService<IReviewQueue>().ApproximateDepth);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard", ct)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard", ct)).StatusCode);
        Assert.Equal(1, factory.Source.InventoryFetches);
    }

    [Fact]
    public async Task Dashboard_projects_fresh_tracker_over_persisted_same_run_without_exposing_errors()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        var pr = new PrKey("org", "project", "repo", 1);
        var id = Guid.NewGuid();
        factory.Source.Inventory = new([new(new(pr, factory.Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var store = factory.Services.GetRequiredService<IFindingStore>();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.SaveRunAsync(new(id, pr, "head-sha", ReviewKind.Full, start, start.AddMinutes(1), true,
            [new("carried", "rule", "high", "title", null, null, null), new("thread-42", "audit", "high", "audit", null, null, null)]), ct);
        var tracker = factory.Services.GetRequiredService<RunTracker>();
        tracker.Register(new(id, pr, start, HeadSha: "head-sha"));
        tracker.Set(id, pr, RunState.Failed, "secret-exception-token");
        var body = await client.GetStringAsync("/reviews/dashboard", ct);
        using var json = JsonDocument.Parse(body);
        var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
        Assert.False(row.GetProperty("currentHeadReviewed").GetBoolean());
        Assert.Equal(1, row.GetProperty("latestAttempt").GetProperty("reviewFindings").GetInt32());
        Assert.Equal(id, row.GetProperty("latestAttempt").GetProperty("runId").GetGuid());
        Assert.DoesNotContain("secret-exception-token", body);
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("needsAttention").GetInt32());
    }

    [Fact]
    public async Task Dashboard_uses_status_budget_and_guid_route_still_matches()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithStatusLimit(2, 60);
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{Guid.NewGuid()}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/dashboard", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/reviews/dashboard", ct)).StatusCode);
    }
}
