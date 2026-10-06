using System.Diagnostics.CodeAnalysis;
using LukasMoeller.Configuration.Toml;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using OpenTelemetry.Logs;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service;
using HostOptions = ReviewForge.Service.HostOptions;

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
var otlpStatus = ServiceCollectionExtensions.ComputeOtlpStatus(builder.Configuration);
var otlpLogsEnabled = otlpStatus.LogsEnabled;
var otlpTracesEnabled = otlpStatus.TracesEnabled;
var otlpMetricsEnabled = otlpStatus.MetricsEnabled;
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    if (otlpLogsEnabled)
        options.AddOtlpExporter();
});
builder.Services.AddHealthChecks()
    .AddCheck<StoreHealthCheck>("finding-store")
    .AddCheck<LivenessHealthCheck>(LivenessHealthCheck.Name);
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

var effectiveOtlpStatus = app.Services.GetRequiredService<OtlpStatus>();
app.Logger.LogInformation(
    "OTLP logs exporter enabled: {LogsEnabled}; endpoint: {LogsEndpoint}; " +
    "traces exporter enabled: {TracesEnabled}; endpoint: {TracesEndpoint}; " +
    "metrics exporter enabled: {MetricsEnabled}; endpoint: {MetricsEndpoint}",
    effectiveOtlpStatus.LogsEnabled,
    effectiveOtlpStatus.LogsEnabled ? effectiveOtlpStatus.EffectiveLogsEndpoint ?? "(SDK default)" : "(none)",
    effectiveOtlpStatus.TracesEnabled,
    effectiveOtlpStatus.TracesEnabled ? effectiveOtlpStatus.EffectiveTracesEndpoint ?? "(SDK default)" : "(none)",
    effectiveOtlpStatus.MetricsEnabled,
    effectiveOtlpStatus.MetricsEnabled ? effectiveOtlpStatus.EffectiveMetricsEndpoint ?? "(SDK default)" : "(none)");
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    // Defense-in-depth for a JSON API that browsers can be pointed at.
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});
app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions
{
    Predicate = check => check.Name == LivenessHealthCheck.Name,
});
app.MapReviewForgeEndpoints();
app.MapDocsEndpoints();
app.Run();

// Exposed for WebApplicationFactory in tests; startup wiring itself is not unit-tested.
[ExcludeFromCodeCoverage]
public partial class Program;