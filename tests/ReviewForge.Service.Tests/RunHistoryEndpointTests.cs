using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class RunHistoryEndpointTests
{
    [Theory]
    [InlineData("offset=-1")]
    [InlineData("offset=10001")]
    [InlineData("count=0")]
    [InlineData("count=101")]
    [InlineData("org=o")]
    [InlineData("org=o&project=p&repositoryId=r")]
    [InlineData("org=o&project=p&repositoryId=r&prId=0")]
    [InlineData("org=&project=p&repositoryId=r&prId=1")]
    public async Task History_rejects_invalid_paging_and_partial_scopes(string query)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/reviews/history?{query}", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task History_requires_authentication_and_shares_status_budget()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithStatusLimit(1, 600);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/history", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/reviews/history", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Anonymous_history_cannot_use_query_key_or_invalid_header()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(ApiKeyOptions.HeaderName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/history?api_key=test-key-1", TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/history", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Accepted_start_is_visible_before_inventory_or_store_catches_up()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var submitted = await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", new SubmitReviewRequest("new", "p", "r", 5), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var accepted = (await submitted.Content.ReadFromJsonAsync<SubmitReviewResponse>(TestContext.Current.CancellationToken))!;
        var history = (await client.GetFromJsonAsync<RunWorklistResponse>("/reviews/history?attentionOnly=true", TestContext.Current.CancellationToken))!;
        Assert.Contains(history.Items, i => i.Run.RunId == accepted.RunId && i.NeedsAttention);
    }

    [Fact]
    public async Task History_is_inventory_independent_and_scoped_by_full_identity()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IFindingStore>();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await store.SaveRunAsync(new(id, new("closed-org", "p", "r", 42), "head", ReviewForge.Core.Domain.ReviewKind.Full,
            now, now, false, []), TestContext.Current.CancellationToken);
        var result = await client.GetFromJsonAsync<RunWorklistResponse>("/reviews/history?offset=0&count=50&attentionOnly=true&org=closed-org&project=p&repositoryId=r&prId=42", TestContext.Current.CancellationToken);
        Assert.Equal(id, Assert.Single(result!.Items).Run.RunId);
        Assert.Null(result.Items[0].Title);
        Assert.Null(result.Items[0].Findings);
        Assert.Empty((await client.GetFromJsonAsync<RunWorklistResponse>("/reviews/history?org=other&project=p&repositoryId=r&prId=42", TestContext.Current.CancellationToken))!.Items);
    }
}
