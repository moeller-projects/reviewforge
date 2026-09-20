using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using ReviewForge.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole(options => options.FormatterName = CompactConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CompactConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.AddOtlpExporter();
});
builder.Services.AddHealthChecks()
    .AddCheck<StoreHealthCheck>("finding-store");
builder.Services.AddReviewForge(builder.Configuration);
builder.Services.AddOpenApi(ApiDocsRegistration.Configure);

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions {Predicate = _ => false});
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;