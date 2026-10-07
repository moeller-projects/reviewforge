using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    private static IServiceCollection AddReviewForgeTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpStatus = ComputeOtlpStatus(configuration);
        services.AddSingleton(otlpStatus);
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: "reviewforge", serviceInstanceId: Environment.MachineName));
        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource(ReviewForgeTelemetry.SourceName);
            if (otlpStatus.TracesEnabled)
                tracing.AddOtlpExporter();
        });
        otel.WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddMeter(ReviewForgeTelemetry.SourceName);
            if (otlpStatus.MetricsEnabled)
                metrics.AddOtlpExporter();
        });
        return services;
    }
}