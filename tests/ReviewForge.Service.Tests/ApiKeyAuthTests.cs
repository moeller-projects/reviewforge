using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Service.Security;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public class ApiKeyAuthTests
{
    // ---- validator unit tests ----

    [Fact]
    public void Validator_accepts_configured_key()
        => Assert.True(ApiKeyValidator.IsValid("k", ["k", "other"]));

    [Fact]
    public void Validator_rejects_wrong_key()
        => Assert.False(ApiKeyValidator.IsValid("nope", ["k"]));

    [Fact]
    public void Validator_rejects_when_no_keys_configured()
        => Assert.False(ApiKeyValidator.IsValid("k", []));

    [Fact]
    public void Validator_rejects_null_or_empty()
    {
        Assert.False(ApiKeyValidator.IsValid(null, ["k"]));
        Assert.False(ApiKeyValidator.IsValid("", ["k"]));
        Assert.False(ApiKeyValidator.IsValid("   ", ["k"]));
    }

    [Fact]
    public void Validator_accepts_any_of_multiple_keys()
    {
        Assert.True(ApiKeyValidator.IsValid("a", ["a", "b", "c"]));
        Assert.True(ApiKeyValidator.IsValid("b", ["a", "b", "c"]));
        Assert.True(ApiKeyValidator.IsValid("c", ["a", "b", "c"]));
    }

    // ---- integration tests ----

    private static async Task<HttpStatusCode> PostReview(HttpClient client, string? apiKey, int prId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews")
        {
            Content = JsonContent.Create(new {org = "o", project = "p", repositoryId = "r", prId}),
        };
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiKeyOptions.HeaderName, apiKey);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task Reviews_endpoints_return_401_without_api_key()
    {
        using var factory = new ReviewForgeFactory();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await PostReview(client, null, 1));

        using var discover = await client.PostAsJsonAsync("/reviews/discover", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, discover.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/reviews/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Reviews_endpoints_return_401_with_invalid_key()
    {
        using var factory = new ReviewForgeFactory();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, await PostReview(client, "wrong", 1));
    }

    [Fact]
    public async Task Second_configured_key_also_authenticates()
    {
        using var factory = new ReviewForgeFactory();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "test-key-2", 42));
    }

    [Fact]
    public async Task Health_and_alive_do_not_require_api_key()
    {
        using var factory = new ReviewForgeFactory();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_after_permit_exhausted()
    {
        using var factory = new ReviewForgeFactory().WithSubmitLimit(2, 600).WithoutWorkers();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "test-key-1", 1));
        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "test-key-1", 2));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostReview(client, "test-key-1", 3));
    }

    [Fact]
    public async Task Rate_limit_partitions_per_key()
    {
        using var factory = new ReviewForgeFactory().WithSubmitLimit(2, 600).WithoutWorkers();
        using var clientA = factory.CreateClient();
        using var clientB = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(clientA, "test-key-1", 1));
        Assert.Equal(HttpStatusCode.Accepted, await PostReview(clientA, "test-key-1", 2));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostReview(clientA, "test-key-1", 3));
        Assert.Equal(HttpStatusCode.Accepted, await PostReview(clientB, "test-key-2", 4));
    }

    [Fact]
    public void Startup_fails_closed_without_keys()
    {
        using var factory = new ReviewForgeFactory().WithoutApiKeys();
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Development_optout_allows_requests()
    {
        using var factory = new ReviewForgeFactory().WithDevelopmentOptOut().WithoutWorkers();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, null, 1));
    }

    [Fact]
    public async Task Development_optout_ignores_arbitrary_keys_for_rate_partitioning()
    {
        using var factory = new ReviewForgeFactory()
            .WithDevelopmentOptOut()
            .WithSubmitLimit(2, 600)
            .WithoutWorkers();
        using var client = factory.Server.CreateClient();

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "arbitrary-1", 1));
        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "arbitrary-2", 2));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostReview(client, "arbitrary-3", 3));
    }

    // ---- middleware unit tests (fail-closed branch is unreachable through the host,
    // which refuses to start without keys) ----

    [Fact]
    public async Task Middleware_fails_closed_when_no_keys_and_no_optout()
    {
        var opts = Options.Create(new ApiKeyOptions {Keys = []});
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ => throw new InvalidOperationException("next must not be called"),
            opts,
            new HostingEnvironment {EnvironmentName = Environments.Production},
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(Environments.Development, true)]
    [InlineData(Environments.Production, false)]
    public async Task Unauthenticated_optout_requires_development_environment(string environmentName, bool shouldCallNext)
    {
        var called = false;
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            Options.Create(new ApiKeyOptions {Keys = [], AllowUnauthenticatedForDevelopment = true}),
            new HostingEnvironment {EnvironmentName = environmentName},
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);

        await middleware.InvokeAsync(new DefaultHttpContext {Request = {Path = "/reviews"}});

        Assert.Equal(shouldCallNext, called);
    }

    [Fact]
    public async Task Middleware_passes_non_reviews_paths_through()
    {
        var opts = Options.Create(new ApiKeyOptions {Keys = ["k"]});
        var called = false;
        var middleware = new ApiKeyAuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            opts,
            new HostingEnvironment {EnvironmentName = Environments.Production},
            NullLogger<ApiKeyAuthenticationMiddleware>.Instance);
        var context = new DefaultHttpContext {Request = {Path = "/health"}};

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }
}
