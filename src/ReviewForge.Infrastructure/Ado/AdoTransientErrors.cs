using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Services.WebApi;

namespace ReviewForge.Infrastructure.Ado;

/// <summary>
/// Classifies ADO SDK exceptions for transient-failure retries. The SDK surfaces
/// <see cref="VssServiceResponseException.HttpStatusCode"/> but not response headers,
/// so Retry-After recovery is a best-effort probe.
/// </summary>
internal static class AdoTransientErrors
{
    private static readonly Regex RetryAfterMessage =
        new(@"retry-after\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsTransient(Exception ex) => ex switch
    {
        // Client-side timeout (the caller's ct is checked separately by the policy).
        TaskCanceledException => true,
        // Throttling or server-side failure.
        VssServiceResponseException { HttpStatusCode: var code }
            => (int)code == 429 || (int)code >= 500,
        // Connection-level failures that escape the SDK wrapper.
        HttpRequestException => true,
        _ => false,
    };

    public static TimeSpan? ProbeRetryAfter(Exception ex)
    {
        // The ADO SDK does not expose response headers on VssServiceResponseException.
        // Best-effort sources only; null means "use computed backoff".
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Data["Retry-After"] is string dataValue
                && int.TryParse(dataValue, out var dataSeconds)
                && dataSeconds >= 0)
            {
                return TimeSpan.FromSeconds(dataSeconds);
            }
        }

        var match = RetryAfterMessage.Match(ex.Message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
