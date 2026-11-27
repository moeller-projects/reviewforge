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

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public void Service_uses_one_https_origin_in_every_environment(string environment)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        Assert.Equal("https://localhost:9090",
            host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Urls"]);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task Build_output_serves_framework_and_workbench_assets_in_every_environment(string environment)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = Browser(host);
        foreach (var asset in new[] { "/dashboard.css", "/_framework/blazor.web.js", "/dashboard-theme.js", "/dashboard-dialog.js" })
        {
            using var response = await client.GetAsync(asset, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(Ct));
        }
    }

    [Fact]
    public async Task Antiforgery_pages_are_noncacheable_without_header_override_warnings()
    {
        var logs = new List<string>();
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithLogCollector(logs);
        using var client = Browser(factory);
        using var response = await client.GetAsync("/dashboard/login", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
        Assert.DoesNotContain(logs, line => line.Contains("headers have been overridden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dashboard_head_and_page_title_do_not_cross_render_mode_boundaries()
    {
        var logs = new List<string>();
        using var factory = new ReviewForgeFactory().WithoutWorkers().WithLogCollector(logs);
        using var client = Browser(factory);
        using var login = await Login(client, await Token(client));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var html = await client.GetStringAsync("/dashboard", Ct);
        Assert.Contains("<title>ReviewForge dashboard</title>", html);
        Assert.DoesNotContain(logs, line => line.Contains("Sections cannot connect across render mode boundaries", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/", "/dashboard")]
    [InlineData("/dashboard", "/dashboard/login")]
    [InlineData("/dashboard/", "/dashboard/login")]
    public async Task Anonymous_page_navigation_redirects_to_dashboard_then_login(string path, string destination)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        using var response = await client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(destination, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Invalid_cookie_redirects_page_but_not_api_or_connection_requests()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-ReviewForge=invalid");
        using var page = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/dashboard/login", page.Headers.Location?.OriginalString);
        foreach (var path in new[] { "/reviews/dashboard", "/dashboard/session", "/_blazor?id=missing" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        using var negotiate = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.Null(negotiate.Headers.Location);
        using var post = await client.PostAsync("/dashboard", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Null(post.Headers.Location);
    }

    [Fact]
    public async Task Anonymous_shell_and_circuit_are_protected_and_login_requires_csrf()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        using var page = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/dashboard/login", page.Headers.Location?.OriginalString);
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
        foreach (var asset in new[] { "/dashboard-http.js", "/dashboard.css", "/dashboard-theme.js", "/_framework/blazor.web.js" })
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
        using var root = await client.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/dashboard", root.Headers.Location?.OriginalString);
        var shell = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        var html = await shell.Content.ReadAsStringAsync(Ct);
        Assert.Contains("ReviewForge dashboard", html);
        Assert.Contains("Loading runs", html);
        Assert.Contains("dashboard.css", html);
        Assert.Contains("dashboard-theme.js", html);
        Assert.DoesNotContain("Radzen", html);
        Assert.True(html.IndexOf("dashboard-theme.js", StringComparison.Ordinal) < html.IndexOf("dashboard.css", StringComparison.Ordinal));
        Assert.Contains("__RequestVerificationToken", html);
        Assert.DoesNotContain("test-key", html);
        Assert.True(shell.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", shell.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/reviews/{Guid.NewGuid()}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews/discover", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/resolutions", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/mcp", new { }, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/reviews/dashboard", Ct)).StatusCode);
        Assert.DoesNotContain(logs, message => message.Contains("test-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_detail_shell_and_history_require_auth_and_allow_only_intended_session_reads()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        var id = Guid.NewGuid();
        var history = $"/reviews/{id}/decisions/history?findingId=key";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync($"/dashboard/runs/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/dashboard/start", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/dashboard/session", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(history, Ct)).StatusCode);
        await Login(client, await Token(client));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/dashboard/runs/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(history, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(history, Ct)).StatusCode);
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/reviews/{runId:guid}/decisions/history");
        Assert.NotNull(endpoint.Metadata.GetMetadata<DashboardSessionAccess>());
        Assert.Equal(ApiKeyOptions.StatusPolicy, endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName);
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
        using var page = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/dashboard/login", page.Headers.Location?.OriginalString);
        using var replayPage = await replay.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, replayPage.StatusCode);
        Assert.Equal("/dashboard/login", replayPage.Headers.Location?.OriginalString);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "test-key-1");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.Accepted, (await HttpClientJsonExtensions.PostAsJsonAsync(client, "/reviews", new { org = "o", project = "p", repositoryId = "r", prId = 92 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Technician_routes_require_session_csrf_and_bad_header_is_not_rescued()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        await Login(client, await Token(client));
        var id = Guid.NewGuid();
        foreach (var path in new[] { $"/reviews/{id}", $"/reviews/{id}/details", "/reviews/drafts/capability", "/reviews/history?offset=0&count=50&attentionOnly=false" })
        {
            Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Ct)).StatusCode);
            client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path, Ct)).StatusCode);
            client.DefaultRequestHeaders.Remove(ApiKeyOptions.HeaderName);
        }
        var finding = new ReviewForge.Core.Domain.RichFinding { RuleId = "r", Title = "t", Severity = "high", Category = "bug", Description = "claim" };
        var observation = ReviewForge.Core.Domain.FindingObservation.Create(finding);
        await factory.Services.GetRequiredService<ReviewForge.Core.Ports.IFindingStore>().SaveRunAsync(new(id,
            new("o", "p", "r", 4), "head", ReviewForge.Core.Domain.ReviewKind.Full, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, true, [], Pipeline: "ReviewDraft", FindingObservations: [observation]), Ct);
        foreach (var path in new[] { "/reviews/drafts", $"/reviews/{id}/decisions" })
        {
            object body = path.EndsWith("decisions", StringComparison.Ordinal)
                ? new SetFindingDecisionRequest(observation.Id.ToString("N"), "head", ReviewForge.Core.Domain.FindingDecisionState.Approved, null, 0)
                : new SubmitReviewRequest("o", "p", "r", 1);
            Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, body, Ct)).StatusCode);
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Token(client));
            Assert.NotEqual(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, body, Ct)).StatusCode);
            client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, body, Ct)).StatusCode);
            client.DefaultRequestHeaders.Remove(ApiKeyOptions.HeaderName);
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        }
        var latest = Assert.Single(await factory.Services.GetRequiredService<ReviewForge.Core.Ports.IFindingDecisionStore>().GetFindingDecisionsAsync(id, Ct));
        Assert.Equal("dashboard-session", latest.Actor);
        Assert.Empty(factory.Source.PostedFindings);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("reconcile")]
    public async Task Publication_requires_authentication_csrf_and_preserves_header_priority(string action)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = Browser(factory);
        var path = $"/reviews/{Guid.NewGuid()}/{action}";
        object request = action == "publish" ? new PublishSelectedDraftRequest("head", [new("id", 1)]) : new ReconcileDraftRequest(["id"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        await Login(client, await Token(client));
        Assert.Equal(HttpStatusCode.BadRequest, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", await Token(client));
        Assert.Equal(HttpStatusCode.NotFound, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        client.DefaultRequestHeaders.Remove(ApiKeyOptions.HeaderName);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, "test-key-1");
        Assert.Equal(HttpStatusCode.NotFound, (await HttpClientJsonExtensions.PostAsJsonAsync(client, path, request, Ct)).StatusCode);
        Assert.Empty(factory.Source.PostedFindings);
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
        using var page = await client.GetAsync("/dashboard", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/dashboard/login", page.Headers.Location?.OriginalString);
        foreach (var path in new[] { "/reviews/dashboard", "/dashboard/session", "/_blazor?id=old" })
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
