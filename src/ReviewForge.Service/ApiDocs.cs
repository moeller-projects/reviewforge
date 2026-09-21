using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace ReviewForge.Service;

/// <summary>OpenAPI document transformer plus opt-in docs endpoints (OpenAPI + Scalar UI).</summary>
public static class ApiDocsRegistration
{
    /// <summary>Registers a document transformer that applies <see cref="ApiDocsOptions"/> metadata.</summary>
    public static void Configure(OpenApiOptions options)
        => options.AddDocumentTransformer(ApplyInfo);

    private static Task ApplyInfo(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        var options = context.ApplicationServices.GetRequiredService<IOptions<ApiDocsOptions>>().Value;
        document.Info.Title = options.Title;
        document.Info.Version = options.Version;
        document.Info.Description = "Automated pull-request review as a service.";
        return Task.CompletedTask;
    }

    /// <summary>Maps /openapi/{doc}.json and /scalar/{doc} only when ApiDocs:Enabled is true.</summary>
    public static WebApplication MapDocsEndpoints(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ApiDocsOptions>>().Value.Enabled)
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }
}