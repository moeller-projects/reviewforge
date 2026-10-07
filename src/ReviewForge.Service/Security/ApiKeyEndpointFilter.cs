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
        if (ApiKeyGate.Evaluate(options.Value, environment, context.HttpContext.Request) is { } rejection)
        {
            if (rejection.Status == StatusCodes.Status503ServiceUnavailable)
            {
                logger.LogError("no API keys configured; refusing /reviews request (fail closed)");
            }
            else
            {
                logger.LogWarning("rejected {Method} {Path}: missing or invalid API key",
                    context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                context.HttpContext.Response.Headers.WWWAuthenticate = "ApiKey";
            }

            return Results.Json(new {error = rejection.Error}, statusCode: rejection.Status);
        }

        return await next(context);
    }
}