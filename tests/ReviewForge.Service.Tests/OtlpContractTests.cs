using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ReviewForge.Service.Tests;

/// <summary>Captures every formatted log message into a list, for startup-log assertions.</summary>
public sealed class CollectingLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(sink);

    public void Dispose()
    {
    }

    private sealed class CollectingLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Add(formatter(state, exception));
    }
}

public class OtlpConfigurationTests
{
    [Theory]
    [InlineData(true, null, null)]
    [InlineData(false, "http://collector:4317", null)]
    [InlineData(false, null, "http://collector:4318")]
    public void Exporter_is_enabled_by_config_common_or_signal_endpoint(
        bool configured,
        string? commonEndpoint,
        string? signalEndpoint)
        => Assert.True(ServiceCollectionExtensions.ShouldEnableOtlpExporter(
            configured, commonEndpoint, signalEndpoint));

    [Fact]
    public void Exporter_is_disabled_without_any_endpoint()
        => Assert.False(ServiceCollectionExtensions.ShouldEnableOtlpExporter(false, null, " "));
}

[Collection("ReviewForge service host")]
public class OtlpEndpointLogTests : IAsyncLifetime
{
    private readonly List<string> _Logs = [];
    private readonly ReviewForgeFactory _Factory = new();

    public OtlpEndpointLogTests()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://example:4317");
        _Factory.WithLogCollector(_Logs);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Startup_logs_the_effective_otlp_endpoint()
    {
        using var client = _Factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_Logs, line => line.Contains("OTLP logs exporter enabled: True") && line.Contains("http://example:4317"));
    }
}

[Collection("ReviewForge service host")]
public class OtlpEndpointDisabledLogTests : IAsyncLifetime
{
    private readonly List<string> _Logs = [];
    private readonly ReviewForgeFactory _Factory = new();

    public OtlpEndpointDisabledLogTests()
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        _Factory.WithLogCollector(_Logs);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _Factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Startup_logs_disabled_when_endpoint_unset()
    {
        using var client = _Factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_Logs, line => line.Contains("OTLP logs exporter enabled: False") && line.Contains("(none)"));
    }
}
