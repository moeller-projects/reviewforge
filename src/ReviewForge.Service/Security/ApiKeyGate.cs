using Microsoft.Extensions.Primitives;

namespace ReviewForge.Service.Security;

/// <summary>
/// Shared X-Api-Key evaluation behind <see cref="ApiKeyEndpointFilter"/> (route-handler
/// endpoints) and <see cref="ApiKeyMiddleware"/> (the MCP transport endpoint, where endpoint
/// filters cannot run). Fails closed: no configured keys and no explicit development
/// opt-out → 503.
/// </summary>
internal static class ApiKeyGate
{
    /// <summary>Null when the request may proceed; otherwise the status code and error message.</summary>
    /// <param name="options">Configured API keys and the development opt-out.</param>
    /// <param name="environment">Host environment; gates the development opt-out.</param>
    /// <param name="request">The incoming request to authenticate.</param>
    /// <param name="allowQueryKey">MCP only: also accept the key as the <c>api_key</c> query
    /// parameter for clients that cannot set headers. The header is preferred; either valid
    /// credential authenticates. Note the tradeoff: query strings appear in request logs, so
    /// header auth should be used whenever the client supports it.</param>
    public static (int Status, string Error)? Evaluate(
        ApiKeyOptions options, IHostEnvironment environment, HttpRequest request, bool allowQueryKey = false)
    {
        if (options.Keys.Length == 0)
        {
            return options.AllowUnauthenticatedForDevelopment
                   && environment.IsEnvironment(Environments.Development)
                ? null
                : (StatusCodes.Status503ServiceUnavailable, "API authentication is not configured");
        }

        if (IsValidKey(request.Headers[ApiKeyOptions.HeaderName], options.Keys))
        {
            return null;
        }

        if (allowQueryKey && IsValidKey(request.Query[ApiKeyOptions.QueryKeyName], options.Keys))
        {
            return null;
        }

        return (StatusCodes.Status401Unauthorized,
            allowQueryKey
                ? $"a valid {ApiKeyOptions.HeaderName} header or {ApiKeyOptions.QueryKeyName} query parameter is required"
                : "a valid X-Api-Key header is required");
    }

    private static bool IsValidKey(StringValues presented, string[] keys)
        => presented.Count == 1 && ApiKeyValidator.IsValid(presented[0], keys);
}