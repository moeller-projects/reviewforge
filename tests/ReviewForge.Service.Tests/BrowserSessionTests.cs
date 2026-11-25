using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public class BrowserSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static HttpClient Browser(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Remove(ApiKeyOptions.HeaderName);
        return client;
    }

    private static async Task<string> Token(HttpClient client)
    {
        var html = await client.GetStringAsync("/dashboard/login", Ct);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string token, string key = "test-key-1")
        => client.PostAsync("/dashboard/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["apiKey"] = key,
        }), Ct);

    [Fact]
    public async Task Anonymous_shell_and_circuit_are_protected_and_login_requires_csrf()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/_blazor?id=missing", Ct)).StatusCode);
        var hubEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/_blazor", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(hubEndpoints);
        Assert.All(hubEndpoints, endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Token(client), "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(client, await Token(client), "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard?api_key=test-key-1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/session", Ct)).StatusCode);
        foreach (var asset in new[] { "/dashboard-http.js", "/dashboard.css", "/_content/Radzen.Blazor/Radzen.Blazor.js", "/_framework/blazor.web.js" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(asset, Ct)).StatusCode);
        using var insecure = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await insecure.GetAsync("/dashboard/login", Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_cookie_is_secure_shell_renders_and_scope_is_narrow()
    {
        var logs = new List<string>();
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithLogCollector(logs);
        using var client = Browser(factory);
        var login = await Login(client, await Token(client));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-key", cookie);
        var cookieValue = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-ReviewForge=", StringComparison.Ordinal))
            .Split(';')[0].Split('=', 2)[1];
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(DashboardSessions.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(cookieValue)!;
        Assert.False(ticket.Properties.IsPersistent);
        Assert.False(ticket.Properties.AllowRefresh);
        Assert.InRange(ticket.Properties.ExpiresUtc!.Value - ticket.Properties.IssuedUtc!.Value,
            TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(8));
        Assert.Equal(2, ticket.Principal.Claims.Count());
        Assert.DoesNotContain(ticket.Principal.Claims, claim => claim.Value.Contains("test-key", StringComparison.Ordinal));
        var shell = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var html = await shell.Content.ReadAsStringAsync(Ct);
        Assert.Contains("ReviewForge dashboard", html);
        Assert.Contains("Loading pull requests", html);
        Assert.Contains("dashboard.css", html);
        Assert.Contains("Radzen.Blazor", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.DoesNotContain("test-key", html);
        Assert.True(shell.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", shell.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/reviews/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews/discover", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/resolutions", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/mcp", new { }, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.DoesNotContain(logs, message => message.Contains("test-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cookie_submit_requires_csrf_logout_revokes_session_and_header_still_works()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        var login = await Login(client, await Token(client));
        var replayCookie = login.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-ReviewForge=", StringComparison.Ordinal)).Split(';')[0];
        var body = new { org = "o", project = "p", repositoryId = "r", prId = 91, forceReview = true };
        Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", body, Ct)).StatusCode);
        var token = await Token(client);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.Accepted, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", body, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", body, Ct)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/dashboard/logout", null, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/dashboard/logout", null, Ct)).StatusCode);
        using var replay = Browser(factory);
        replay.DefaultRequestHeaders.Add("Cookie", replayCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard", Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "test-key-1");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.Accepted, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", new { org = "o", project = "p", repositoryId = "r", prId = 92 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Fixed_expiry_blocks_page_negotiate_read_and_submit()
    {
        using var original = new ReviewForgeFactory().WithoutWorkers();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = Browser(factory);
        await Login(client, await Token(client));
        clock.Advance(TimeSpan.FromHours(8));
        foreach (var path in new[] { "/dashboard", "/reviews/dashboard", "/dashboard/session", "/_blazor?id=old" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", new { org = "o", project = "p", repositoryId = "r", prId = 1 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Cookie_submissions_preserve_validation_queue_capacity_and_rate_limits()
    {
        using var original = new ReviewForgeFactory().WithoutWorkers().WithSubmitLimit(3, 600);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ReviewForge.Core.Ports.IReviewQueue>(new ReviewQueue(1))));
        using var client = Browser(factory);
        await Login(client, await Token(client));
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Token(client));
        Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews",
            new SubmitReviewRequest("", "p", "r", 1), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews",
            new SubmitReviewRequest("o", "p", "r", 1, true), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews",
            new SubmitReviewRequest("o", "p", "r", 2), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews",
            new SubmitReviewRequest("o", "p", "r", 3), Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_is_throttled_and_session_reads_keep_status_rate_limit()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithStatusLimit(1, 600);
        using var client = Browser(factory);
        var token = await Token(client);
        await Login(client, token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.BadRequest, (await Login(client, "", "bad")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(client, "", "bad")).StatusCode);
    }
}
