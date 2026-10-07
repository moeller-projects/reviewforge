using Microsoft.Extensions.Options;

namespace ReviewForge.Service.Security;

/// <summary>
/// X-Api-Key enforcement as route-branch middleware for the <c>/mcp</c> surface. The MCP
/// transport endpoint is a raw RequestDelegate — endpoint filters (the mechanism guarding
/// <c>/reviews</c>) never run on it, so the same check lives here via <see cref="ApiKeyGate"/>.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options, IHostEnvironment environment, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Evaluate BEFORE next: the MCP transport must never execute unauthenticated, and
        // authenticated requests must execute exactly once.
        if (ApiKeyGate.Evaluate(options.Value, environment, context.Request, allowQueryKey: true) is { } rejection)
        {
            if (rejection.Status == StatusCodes.Status503ServiceUnavailable)
            {
                logger.LogError("no API keys configured; refusing {Method} {Path} (fail closed)",
                    context.Request.Method, context.Request.Path);
            }
            else
            {
                logger.LogWarning("rejected {Method} {Path}: missing or invalid API key",
                    context.Request.Method, context.Request.Path);
                context.Response.Headers.WWWAuthenticate = "ApiKey";
            }

            context.Response.StatusCode = rejection.Status;
            await context.Response.WriteAsJsonAsync(new {error = rejection.Error}, context.RequestAborted);
            return;
        }

        await next(context);
    }
}