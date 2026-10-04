using System.Diagnostics.CodeAnalysis;
using LukasMoeller.Configuration.Toml;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddTomlFile("config.toml", optional: true, reloadOnChange: true);
builder.Configuration.AddTomlFile(
    $"config.{builder.Environment.EnvironmentName}.toml",
    optional: true,
    reloadOnChange: true);
// Reapply higher-precedence providers after TOML; CreateBuilder registered them before this file.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
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

// Startup recovery for run-scoped private checkouts: at process start no private checkout
// can be live (leases are process-lifetime), so everything under {root}/private is orphaned
// by construction. Runs before the hosted workers start.
{
    var pool = app.Services.GetRequiredService<RepoCheckoutPool>();
    var reaped = pool.ReapOrphanedPrivateCheckouts();
    if (reaped > 0)
    {
        app.Logger.LogInformation("startup recovery reaped {Count} orphaned private checkout(s)", reaped);
    }
}

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
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    // Defense-in-depth for a JSON API that browsers can be pointed at.
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions {Predicate = _ => false});
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;