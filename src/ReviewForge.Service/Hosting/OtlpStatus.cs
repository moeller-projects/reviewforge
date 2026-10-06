namespace ReviewForge.Service;

/// <summary>Effective OTLP exporter policy and endpoints for each signal.</summary>
public sealed record OtlpStatus(
    bool LogsEnabled,
    bool TracesEnabled,
    bool MetricsEnabled,
    string? CommonEndpoint,
    string? LogsEndpoint,
    string? TracesEndpoint,
    string? MetricsEndpoint)
{
    public string? EffectiveLogsEndpoint => LogsEndpoint ?? CommonEndpoint;
    public string? EffectiveTracesEndpoint => TracesEndpoint ?? CommonEndpoint;
    public string? EffectiveMetricsEndpoint => MetricsEndpoint ?? CommonEndpoint;
}
