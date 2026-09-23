using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using ReviewForge.Service.Security;
using Scalar.AspNetCore;

namespace ReviewForge.Service;

/// <summary>OpenAPI document transformer plus opt-in docs endpoints (OpenAPI + Scalar UI).</summary>
public static class ApiDocsRegistration
{
    private const string ApiKeySchemeId = "ApiKey";

    /// <summary>Registers a document transformer that applies <see cref="ApiDocsOptions"/> metadata.</summary>
    public static void Configure(OpenApiOptions options)
        => options.AddDocumentTransformer(ApplyInfo);

    private static Task ApplyInfo(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var options = context.ApplicationServices.GetRequiredService<IOptions<ApiDocsOptions>>().Value;
        document.Info.Title = options.Title;
        document.Info.Version = options.Version;
        document.Info.Description = "Automated pull-request review as a service.";
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[ApiKeySchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = Security.ApiKeyOptions.HeaderName,
            Description = "ReviewForge API key.",
        };

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(ApiKeySchemeId, document, externalResource: null)] = [],
        };
        foreach (var path in document.Paths.Where(
                     pair => pair.Key.StartsWith("/reviews", StringComparison.OrdinalIgnoreCase)))
        {
            if (path.Value?.Operations is not { } operations)
            {
                continue;
            }

            foreach (var operation in operations.Values)
            {
                operation.Security ??= [];
                operation.Security.Add(requirement);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Maps /openapi/{doc}.json and /scalar/{doc} only when ApiDocs:Enabled is true.</summary>
    public static WebApplication MapDocsEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<ApiDocsOptions>>().Value;
        if (options.Enabled)
        {
            var api = app.Services.GetRequiredService<IOptions<Security.ApiKeyOptions>>().Value;
            WarnIfExposed(options, authConfigured: api.Keys.Length > 0,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiDocsRegistration)));
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }

    /// <summary>Warns when the API schema is served while the API itself is unauthenticated.</summary>
    public static void WarnIfExposed(ApiDocsOptions options, bool authConfigured, ILogger logger)
    {
        if (options.Enabled && !authConfigured)
        {
            logger.LogWarning(
                "ApiDocs is enabled (OpenAPI + Scalar UI at /openapi and /scalar) but the API has no " +
                "authentication configured — the docs advertise an unauthenticated attack surface. " +
                "Set ApiDocs:Enabled=false outside trusted dev networks.");
        }
    }
}