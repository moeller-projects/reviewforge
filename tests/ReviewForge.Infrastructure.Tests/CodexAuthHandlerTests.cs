using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class CodexAuthHandlerTests : IDisposable
{
    private readonly string _Dir;

    public CodexAuthHandlerTests()
    {
        _Dir = Path.Combine(Path.GetTempPath(), "reviewforge-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Dir);
    }

    public void Dispose() => Directory.Delete(_Dir, recursive: true);

    private (HttpClient Client, StubHandler Api, StubHandler Refresh, string ValidToken, string RotatedToken) Setup(
        Func<string?, bool> acceptToken)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var validToken = CodexCredentialTests.Jwt(clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds());
        var rotatedToken = CodexCredentialTests.Jwt(clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds());

        var path = Path.Combine(_Dir, "auth.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            tokens = new {access_token = validToken, refresh_token = "r1", account_id = "acc"},
        }));

        var refresh = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = rotatedToken, refresh_token = "r2",
            }), Encoding.UTF8, "application/json"),
        });

        var api = new StubHandler((request, _) =>
            acceptToken(request.Headers.Authorization?.Parameter)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var credential = new CodexCredential(path, refresh, clock);
        var handler = new CodexAuthHandler(credential) {InnerHandler = api};
        return (new HttpClient(handler), api, refresh, validToken, rotatedToken);
    }

    [Fact]
    public async Task Attaches_auth_headers()
    {
        var (client, api, _, validToken, _) = Setup(token => token is not null);
        var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/responses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(api.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(validToken, request.Headers.Authorization.Parameter);
        Assert.Equal("responses=experimental", string.Join(",", request.Headers.GetValues("OpenAI-Beta")));
    }

    [Fact]
    public async Task Unauthorized_triggers_single_force_refresh_and_retry()
    {
        string? rotated = null;
        var (client, api, refresh, _, rotatedToken) = Setup(token => token == rotated);
        rotated = rotatedToken;
        // First call: valid token rejected (401) → force refresh → retry with rotated token (200).
        var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/responses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, api.Requests.Count);
        Assert.Single(refresh.Requests);
        Assert.Equal(rotatedToken, api.Requests[1].Headers.Authorization!.Parameter);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }
}