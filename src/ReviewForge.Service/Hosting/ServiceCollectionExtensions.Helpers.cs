using Microsoft.Extensions.Options;

namespace ReviewForge.Service;

public static partial class ServiceCollectionExtensions
{
    internal static OptionsBuilder<T> AddValidatedOptions<T>(IServiceCollection services, IConfiguration configuration, string sectionName)
        where T : class
        => services.AddOptions<T>().Bind(configuration.GetSection(sectionName)).ValidateDataAnnotations().ValidateOnStart();

    internal static T ParseDefinedEnum<T>(string? value, string settingPath) where T : struct, Enum
    {
        if (value is null)
            throw new InvalidOperationException($"{settingPath} is required here");
        if (!Enum.TryParse<T>(value, true, out var parsed) || !Enum.IsDefined(parsed))
            throw new InvalidOperationException($"{settingPath} must be one of {string.Join(" | ", Enum.GetNames<T>())} (got '{value}')");
        return parsed;
    }

    internal static OtlpStatus ComputeOtlpStatus(IConfiguration configuration)
    {
        var configured = configuration.GetValue<bool?>($"{HostOptions.SectionName}:OtlpEnabled") is true;
        var common = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var logs = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
        var traces = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
        var metrics = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT");
        return new OtlpStatus(ShouldEnableOtlpExporter(configured, common, logs), ShouldEnableOtlpExporter(configured, common, traces),
            ShouldEnableOtlpExporter(configured, common, metrics), common, logs, traces, metrics);
    }

    internal static bool ShouldEnableOtlpExporter(bool configured, string? commonEndpoint, string? signalEndpoint)
        => configured || !string.IsNullOrWhiteSpace(commonEndpoint) || !string.IsNullOrWhiteSpace(signalEndpoint);
}