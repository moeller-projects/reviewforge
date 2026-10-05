namespace ReviewForge.Service;

/// <summary>Host-level concurrency, recovery, and telemetry settings.</summary>
public sealed class HostOptions
{
    public const string SectionName = "Host";

    public int WorkerCount { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Older in-flight run shells are finalized as failures during startup recovery.</summary>
    public int StaleShellMinutes { get; init; } = 10;

    /// <summary>Opt-in switch for OTLP exporters when no OTEL endpoint is set.</summary>
    public bool OtlpEnabled { get; init; }
}
