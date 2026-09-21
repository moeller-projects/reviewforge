using Microsoft.Extensions.Options;

namespace ReviewForge.Service.Security;

/// <summary>
/// Requires a valid X-Api-Key on every /reviews* request. Health/alive endpoints and the
/// opt-in docs surface stay open. Fails closed: no configured keys and no explicit
/// development opt-out → 503 (startup validation normally prevents this state entirely).
/// </summary>
public sealed class ApiKeyAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<ApiKeyOptions> options,
    ILogger<ApiKeyAuthenticationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/reviews", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var opts = options.Value;
        if (opts.Keys.Length == 0)
        {
            if (opts.AllowUnauthenticatedForDevelopment)
            {
                await next(context);
                return;
            }

            logger.LogError("no API keys configured; refusing /reviews request (fail closed)");
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new {error = "API authentication is not configured"});
            return;
        }

        var presented = context.Request.Headers[ApiKeyOptions.HeaderName];
        if (presented.Count != 1 || !ApiKeyValidator.IsValid(presented[0], opts.Keys))
        {
            logger.LogWarning("rejected {Method} {Path}: missing or invalid API key",
                context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            await context.Response.WriteAsJsonAsync(new {error = "a valid X-Api-Key header is required"});
            return;
        }

        await next(context);
    }
}
