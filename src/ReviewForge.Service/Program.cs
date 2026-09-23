using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using ReviewForge.Service;
using ReviewForge.Service.Security;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole(options => options.FormatterName = CompactConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CompactConsoleFormatter, ConsoleFormatterOptions>();
var commonOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var logOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
var traceOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
var metricOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");
var otlpConfigured = builder.Configuration.GetValue<bool?>($"{ReviewForgeServiceOptions.SectionName}:OtlpEnabled") is true;
var otlpLogsEnabled = ServiceCollectionExtensions.ShouldEnableOtlpExporter(otlpConfigured, commonOtlpEndpoint, logOtlpEndpoint);
var otlpTracesEnabled = ServiceCollectionExtensions.ShouldEnableOtlpExporter(otlpConfigured, commonOtlpEndpoint, traceOtlpEndpoint);
var otlpMetricsEnabled = ServiceCollectionExtensions.ShouldEnableOtlpExporter(otlpConfigured, commonOtlpEndpoint, metricOtlpEndpoint);
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    if (otlpLogsEnabled)
        options.AddOtlpExporter();
});
builder.Services.AddHealthChecks()
    .AddCheck<StoreHealthCheck>("finding-store");
builder.Services.AddReviewForge(builder.Configuration);
builder.Services.AddOpenApi(ApiDocsRegistration.Configure);

var app = builder.Build();


app.UseMiddleware<ApiKeyAuthenticationMiddleware>(); // reject unauthenticated before they consume rate budget
app.Logger.LogInformation(
    "OTLP logs exporter enabled: {LogsEnabled}; endpoint: {LogsEndpoint}; " +
    "traces exporter enabled: {TracesEnabled}; endpoint: {TracesEndpoint}; " +
    "metrics exporter enabled: {MetricsEnabled}; endpoint: {MetricsEndpoint}",
    otlpLogsEnabled,
    otlpLogsEnabled ? logOtlpEndpoint ?? commonOtlpEndpoint ?? "(SDK default)" : "(none)",
    otlpTracesEnabled,
    otlpTracesEnabled ? traceOtlpEndpoint ?? commonOtlpEndpoint ?? "(SDK default)" : "(none)",
    otlpMetricsEnabled,
    otlpMetricsEnabled ? metricOtlpEndpoint ?? commonOtlpEndpoint ?? "(SDK default)" : "(none)");
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;