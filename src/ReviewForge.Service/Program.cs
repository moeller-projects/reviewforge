using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using ReviewForge.Service;
using ReviewForge.Service.Security;

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

// OTLP export is env-driven (OTEL_EXPORTER_OTLP_ENDPOINT); log which endpoint is in effect
// at startup. {Endpoint} is operator config, not attacker data — safe at Information.
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
app.Logger.LogInformation("OTLP exporter endpoint: {Endpoint}", otlpEndpoint ?? "(none — export disabled)");

app.UseMiddleware<ApiKeyAuthenticationMiddleware>(); // reject unauthenticated before they consume rate budget
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions {Predicate = _ => false});
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;