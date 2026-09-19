using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ReviewForge.Infrastructure.Codex;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class CodexHttpDebugHandlerTests
{
    [Fact]
    public async Task Logs_request_and_response_without_authorization_header()
    {
        var output = new StringWriter();
        var api = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        });
        var handler = new CodexHttpDebugHandler(output) {InnerHandler = api};
        var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses")
        {
            Content = new StringContent("{\"input\":\"hi\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        request.Headers.TryAddWithoutValidation("X-Trace", "abc");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Body was buffered for logging, then re-buffered so the caller can still read it.
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var log = output.ToString();
        Assert.Contains("[codex-http] request POST https://chatgpt.com/backend-api/codex/responses", log);
        Assert.Contains("X-Trace=abc", log);
        Assert.Contains("{\"input\":\"hi\"}", log);
        Assert.Contains("[codex-http] response 200", log);
        Assert.Contains("{\"ok\":true}", log);
        Assert.DoesNotContain("secret-token", log);
    }

    [Fact]
    public async Task Handles_bodyless_request_and_response_without_content_type()
    {
        var output = new StringWriter();
        var api = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        var handler = new CodexHttpDebugHandler(output) {InnerHandler = api};
        var client = new HttpClient(handler);

        var response = await client.GetAsync("https://chatgpt.com/backend-api/codex/models");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var log = output.ToString();
        Assert.Contains("[codex-http] request GET https://chatgpt.com/backend-api/codex/models", log);
        Assert.Contains("[codex-http] response 204", log);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond());
    }
}