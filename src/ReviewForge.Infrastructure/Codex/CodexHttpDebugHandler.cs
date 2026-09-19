using System.Net.Http.Headers;

namespace ReviewForge.Infrastructure.Codex;

/// <summary>
/// Opt-in wire logger for diagnosing Responses API requests. Authorization headers are never logged.
/// Enable with REVIEWFORGE_DEBUG_CODEX_HTTP=1.
/// </summary>
public sealed class CodexHttpDebugHandler(TextWriter? output = null) : DelegatingHandler
{
    public const string EnvironmentVariable = "REVIEWFORGE_DEBUG_CODEX_HTTP";

    private readonly TextWriter _Output = output ?? Console.Error;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        await _Output.WriteLineAsync($"[codex-http] request {request.Method} {request.RequestUri}");
        await _Output.WriteLineAsync($"[codex-http] request headers {SafeHeaders(request.Headers)}");
        await _Output.WriteLineAsync($"[codex-http] request body {requestBody}");

        var response = await base.SendAsync(request, cancellationToken);
        var responseContentType = response.Content?.Headers.ContentType?.ToString();
        var responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        await _Output.WriteLineAsync($"[codex-http] response {(int) response.StatusCode} {response.ReasonPhrase}");
        await _Output.WriteLineAsync($"[codex-http] response headers {SafeHeaders(response.Headers)}");
        await _Output.WriteLineAsync($"[codex-http] response body {responseBody}");

        response.Content = new StringContent(responseBody);
        if (responseContentType is not null)
        {
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(responseContentType);
        }

        return response;
    }

    private static string SafeHeaders(HttpHeaders headers)
        => string.Join(", ", headers
            .Where(h => !string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}={string.Join(";", h.Value)}"));
}