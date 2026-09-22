using System.Net.Http.Headers;
using System.Text;

namespace ReviewForge.Infrastructure.Codex;

/// <summary>
/// Opt-in wire logger for diagnosing Responses API requests. Bodies are truncated
/// (default 4 KiB, override with <see cref="MaxBytesEnvironmentVariable"/>) and sensitive
/// headers are redacted. Never enabled in Production: ChatClientFactory refuses to attach
/// it there. Enable with <c>REVIEWFORGE_DEBUG_CODEX_HTTP=1</c>.
/// </summary>
public sealed class CodexHttpDebugHandler : DelegatingHandler
{
    public const string EnvironmentVariable = "REVIEWFORGE_DEBUG_CODEX_HTTP";
    public const string MaxBytesEnvironmentVariable = "REVIEWFORGE_DEBUG_CODEX_HTTP_MAXBYTES";
    public const int DefaultMaxBodyBytes = 4096;

    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "chatgpt-account-id", "OpenAI-Beta", "Cookie", "Set-Cookie",
    };

    private readonly TextWriter _Output;
    private readonly int _MaxBodyBytes;

    public CodexHttpDebugHandler(TextWriter? output = null, int? maxBodyBytes = null)
    {
        _Output = output ?? Console.Error;
        _MaxBodyBytes = maxBodyBytes
                        ?? (int.TryParse(Environment.GetEnvironmentVariable(MaxBytesEnvironmentVariable), out var v) && v > 0
                            ? v
                            : DefaultMaxBodyBytes);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        await _Output.WriteLineAsync($"[codex-http] request {request.Method} {request.RequestUri}");
        await _Output.WriteLineAsync($"[codex-http] request headers {SafeHeaders(request.Headers)}");
        await _Output.WriteLineAsync($"[codex-http] request body {Truncate(requestBody)}");

        var response = await base.SendAsync(request, cancellationToken);
        var responseContentType = response.Content?.Headers.ContentType?.ToString();
        var responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        await _Output.WriteLineAsync($"[codex-http] response {(int)response.StatusCode} {response.ReasonPhrase}");
        await _Output.WriteLineAsync($"[codex-http] response headers {SafeHeaders(response.Headers)}");
        await _Output.WriteLineAsync($"[codex-http] response body {Truncate(responseBody)}");

        response.Content = new StringContent(responseBody);
        if (responseContentType is not null)
        {
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(responseContentType);
        }

        return response;
    }

    internal string Truncate(string body)
    {
        var byteCount = Encoding.UTF8.GetByteCount(body);
        if (byteCount <= _MaxBodyBytes)
        {
            return body;
        }

        var length = Math.Min(body.Length, _MaxBodyBytes);
        while (Encoding.UTF8.GetByteCount(body.AsSpan(0, length)) > _MaxBodyBytes)
        {
            length--;
        }

        if (length > 0 && length < body.Length &&
            char.IsHighSurrogate(body[length - 1]) && char.IsLowSurrogate(body[length]))
        {
            length--;
        }

        return $"{body[..length]}... [truncated {Encoding.UTF8.GetByteCount(body.AsSpan(length))} bytes]";
    }

    private static string SafeHeaders(HttpHeaders headers)
        => string.Join(", ", headers.Select(h => RedactedHeaders.Contains(h.Key)
            ? $"{h.Key}=[redacted]"
            : $"{h.Key}={string.Join(";", h.Value)}"));
}