using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class CodexCredentialTests : IDisposable
{
    private readonly string _Dir;

    public CodexCredentialTests()
    {
        _Dir = Path.Combine(Path.GetTempPath(), "reviewforge-codex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Dir);
    }

    public void Dispose() => Directory.Delete(_Dir, recursive: true);

    internal static string Jwt(long exp)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"exp\":{exp}}}")}.sig";
    }

    private string WriteAuth(string accessToken, string refreshToken = "refresh-1")
    {
        var path = Path.Combine(_Dir, "auth.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            tokens = new {access_token = accessToken, refresh_token = refreshToken, account_id = "acc"},
            last_refresh = "2026-01-01T00:00:00Z",
        }));
        return path;
    }

    private static HttpResponseMessage TokenResponse(string accessToken, string refreshToken = "refresh-2")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = accessToken, refresh_token = refreshToken, account_id = "acc",
            }), Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Valid_token_is_returned_without_http_call()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var token = Jwt(clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds());
        var handler = new StubHandler(_ => throw new InvalidOperationException("no http expected"));
        var credential = new CodexCredential(WriteAuth(token), handler, clock);

        Assert.Equal(token, await credential.GetTokenAsync(CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Expiring_token_is_refreshed_and_persisted_atomically()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var oldToken = Jwt(clock.GetUtcNow().AddSeconds(30).ToUnixTimeSeconds());
        var newToken = Jwt(clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds());
        var path = WriteAuth(oldToken);
        var handler = new StubHandler(_ => TokenResponse(newToken));
        var credential = new CodexCredential(path, handler, clock);

        Assert.Equal(newToken, await credential.GetTokenAsync(CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(CodexCredential.TokenEndpoint, request.RequestUri!.ToString());
        var persisted = File.ReadAllText(path);
        Assert.Contains(newToken, persisted);
        Assert.Contains("refresh-2", persisted);
        Assert.False(File.Exists(path + ".tmp")); // temp file moved, not left behind
    }

    [Fact]
    public async Task ForceRefresh_refreshes_even_valid_tokens()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var token = Jwt(clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds());
        var newToken = Jwt(clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds());
        var handler = new StubHandler(_ => TokenResponse(newToken));
        var credential = new CodexCredential(WriteAuth(token), handler, clock);

        Assert.Equal(newToken, await credential.ForceRefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_refresh_surfaces_http_error()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var token = Jwt(clock.GetUtcNow().AddSeconds(10).ToUnixTimeSeconds());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var credential = new CodexCredential(WriteAuth(token), handler, clock);

        await Assert.ThrowsAsync<HttpRequestException>(() => credential.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Empty_refresh_response_throws()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var token = Jwt(clock.GetUtcNow().AddSeconds(10).ToUnixTimeSeconds());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        });
        var credential = new CodexCredential(WriteAuth(token), handler, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => credential.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Missing_or_invalid_file_throws()
    {
        var missing = new CodexCredential(Path.Combine(_Dir, "nope.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => missing.GetTokenAsync(CancellationToken.None));

        var invalid = Path.Combine(_Dir, "invalid.json");
        File.WriteAllText(invalid, "not json");
        await Assert.ThrowsAsync<JsonException>(() => new CodexCredential(invalid).GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public void Jwt_expiry_parsing_edge_cases()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));

        Assert.Null(CodexCredential.TryReadJwtExpiry("not-a-jwt"));
        Assert.Null(CodexCredential.TryReadJwtExpiry("a.!!!invalid!!!.c"));
        Assert.Null(CodexCredential.TryReadJwtExpiry("a." + Convert.ToBase64String("not json"u8.ToArray()) + ".c"));
        var noExp = "h." + Convert.ToBase64String("{\"sub\":\"x\"}"u8.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
        Assert.Null(CodexCredential.TryReadJwtExpiry(noExp));

        // Unreadable tokens count as expiring — refresh is the safe default.
        Assert.True(CodexCredential.ExpiringSoon("garbage", clock));
        Assert.False(CodexCredential.ExpiringSoon(Jwt(clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds()), clock));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}