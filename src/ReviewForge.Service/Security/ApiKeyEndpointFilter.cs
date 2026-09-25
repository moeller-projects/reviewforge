using Microsoft.Extensions.Options;

namespace ReviewForge.Service.Security;

/// <summary>
/// Authoritative API-key enforcement for the /reviews route group. Lives on endpoint
/// metadata so auth matching cannot diverge from routing (route matching is
/// case-insensitive; the former middleware's ordinal path check was bypassable).
/// Fails closed: no configured keys and no explicit development opt-out → 503.
/// </summary>
public sealed class ApiKeyEndpointFilter(
    IOptions<ApiKeyOptions> options,
    IHostEnvironment environment,
    ILogger<ApiKeyEndpointFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var opts = options.Value;
        if (opts.Keys.Length == 0)
        {
            if (opts.AllowUnauthenticatedForDevelopment
                && environment.IsEnvironment(Environments.Development))
            {
                return await next(context);
            }

            logger.LogError("no API keys configured; refusing /reviews request (fail closed)");
            return Results.Json(
                new {error = "API authentication is not configured"},
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var presented = context.HttpContext.Request.Headers[ApiKeyOptions.HeaderName];
        if (presented.Count != 1 || !ApiKeyValidator.IsValid(presented[0], opts.Keys))
        {
            logger.LogWarning("rejected {Method} {Path}: missing or invalid API key",
                context.HttpContext.Request.Method, context.HttpContext.Request.Path);
            context.HttpContext.Response.Headers.WWWAuthenticate = "ApiKey";
            return Results.Json(
                new {error = "a valid X-Api-Key header is required"},
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
