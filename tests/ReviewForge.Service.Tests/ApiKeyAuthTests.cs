using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Service.Security;
using Xunit;

namespace ReviewForge.Service.Tests;

file sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "ReviewForge.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

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

    private static async Task<HttpStatusCode> PostReview(
        HttpClient client, string? apiKey, int prId, string path = "/reviews")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
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
    public async Task Case_variant_reviews_paths_return_401_without_api_key()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.Server.CreateClient();

        // Routing is case-insensitive; the endpoint filter must run on every variant (P1-13).
        Assert.Equal(HttpStatusCode.Unauthorized, await PostReview(client, null, 1, "/REVIEWS"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostReview(client, null, 1, "/ReViews"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/REVIEWS/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/REVIEWS/DISCOVER", new { })).StatusCode);
    }

    [Fact]
    public async Task Case_variant_reviews_paths_accept_valid_key()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient(); // default test-key-1 header

        Assert.Equal(HttpStatusCode.Accepted, await PostReview(client, "test-key-1", 7, "/REVIEWS"));
        // Unknown run id → 404 proves the request passed auth and reached the handler.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/REVIEWS/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Near_miss_reviews_prefix_is_not_routed()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.Server.CreateClient();

        // /reviewsx must not match the group (segment-exact) and must not hit the filter.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/reviewsx")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await PostReview(client, null, 1, "/reviewsfoo"));
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

    // ---- endpoint-filter unit tests (fail-closed branch is unreachable through the host,
    // which refuses to start without keys) ----

    private sealed class TestFilterContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext { get; } = httpContext;
        public override IList<object?> Arguments { get; } = [];
        public override T GetArgument<T>(int index) => throw new NotSupportedException();
    }

    private static async Task<(int StatusCode, bool CalledNext)> InvokeFilterAsync(
        ApiKeyOptions options, string environmentName, string path, string? apiKey)
    {
        var called = false;
        EndpointFilterDelegate next = _ =>
        {
            called = true;
            return ValueTask.FromResult<object?>(Results.Ok());
        };
        var filter = new ApiKeyEndpointFilter(
            Options.Create(options),
            new TestHostEnvironment {EnvironmentName = environmentName},
            NullLogger<ApiKeyEndpointFilter>.Instance);
        var httpContext = new DefaultHttpContext {Request = {Path = path}};
        if (apiKey is not null)
        {
            httpContext.Request.Headers[ApiKeyOptions.HeaderName] = apiKey;
        }

        var result = await filter.InvokeAsync(new TestFilterContext(httpContext), next);

        var statusCode = httpContext.Response.StatusCode;
        if (result is IStatusCodeHttpResult {StatusCode: not null} statusResult)
        {
            statusCode = statusResult.StatusCode.Value;
        }

        return (statusCode, called);
    }

    [Fact]
    public async Task Filter_fails_closed_when_no_keys_and_no_optout()
    {
        var (statusCode, called) = await InvokeFilterAsync(
            new ApiKeyOptions(), Environments.Production, "/reviews", apiKey: null);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCode);
        Assert.False(called);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task Unauthenticated_optout_requires_development_environment(
        string environmentName, bool shouldCallNext)
    {
        var (_, called) = await InvokeFilterAsync(
            new ApiKeyOptions {AllowUnauthenticatedForDevelopment = true},
            environmentName, "/reviews", apiKey: null);

        Assert.Equal(shouldCallNext, called);
    }

    [Fact]
    public async Task Filter_passes_valid_key_to_next()
    {
        var (statusCode, called) = await InvokeFilterAsync(
            new ApiKeyOptions(["k"]), Environments.Production, "/reviews", "k");

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, statusCode);
    }

    [Fact]
    public async Task Filter_rejects_missing_or_invalid_key()
    {
        var (missingStatus, missingCalled) = await InvokeFilterAsync(
            new ApiKeyOptions(["k"]), Environments.Production, "/reviews", apiKey: null);
        var (wrongStatus, wrongCalled) = await InvokeFilterAsync(
            new ApiKeyOptions(["k"]), Environments.Production, "/reviews", "nope");

        Assert.Equal(StatusCodes.Status401Unauthorized, missingStatus);
        Assert.False(missingCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, wrongStatus);
        Assert.False(wrongCalled);
    }
}
