using System.Net;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Alive_remains_healthy_when_finding_store_is_unavailable()
    {
        await using var factory = new ReviewForgeFactory();
        factory.Store.ThrowOnPing = new InvalidOperationException("store unavailable");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_returns_service_unavailable_when_finding_store_is_unavailable()
    {
        await using var factory = new ReviewForgeFactory();
        factory.Store.ThrowOnPing = new InvalidOperationException("store unavailable");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Health_returns_ok_when_finding_store_is_available()
    {
        await using var factory = new ReviewForgeFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}