using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class CompactConsoleFormatterTests
{
    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "CRT")]
    [InlineData((LogLevel) 99, "NON")]
    public void Writes_ascii_level_and_last_source_entry(LogLevel level, string abbreviation)
    {
        var formatter = new CompactConsoleFormatter(new TestOptionsMonitor<ConsoleFormatterOptions>());
        var entry = new LogEntry<string>(
            level,
            "ReviewForge.Service.ReviewWorker",
            new EventId(7),
            "message",
            null,
            static (state, _) => state);
        using var output = new StringWriter();

        formatter.Write(entry, null, output);

        Assert.Matches(
            $"^{abbreviation} \\d{{4}}-\\d{{2}}-\\d{{2}} \\d{{2}}:\\d{{2}}:\\d{{2}} ReviewWorker: message\\r?\\n$",
            output.ToString());
    }

    [Fact]
    public void Flattens_message_but_keeps_exception_on_new_lines()
    {
        var formatter = new CompactConsoleFormatter(new TestOptionsMonitor<ConsoleFormatterOptions>());
        var exception = new InvalidOperationException("bad");
        var entry = new LogEntry<string>(
            LogLevel.Error,
            "ReviewForge.Service.ReviewWorker",
            default,
            "first\nsecond",
            exception,
            static (state, _) => state);
        using var output = new StringWriter();

        formatter.Write(entry, null, output);

        var text = output.ToString();
        Assert.Matches(
            @"^ERR \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} ReviewWorker: first second\r?\n",
            text);
        Assert.Contains(Environment.NewLine + "System.InvalidOperationException: bad", text);
    }

    [Fact]
    public void Writes_scopes_after_message_when_enabled()
    {
        var formatter = new CompactConsoleFormatter(new TestOptionsMonitor<ConsoleFormatterOptions>
        {
            CurrentValue = new ConsoleFormatterOptions {IncludeScopes = true},
        });
        var entry = new LogEntry<string>(
            LogLevel.Information,
            "Worker",
            default,
            "message",
            null,
            static (state, _) => state);
        using var output = new StringWriter();
        var scopes = new LoggerExternalScopeProvider();
        using (scopes.Push("run-1"))
        {
            formatter.Write(entry, scopes, output);
        }

        Assert.Matches(
            @"^INF \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} Worker: message \[run-1\]\r?\n$",
            output.ToString());
    }


    private sealed class TestOptionsMonitor<T>(T? value = null) : IOptionsMonitor<T>
        where T : class, new()
    {
        public T CurrentValue { get; set; } = value ?? new T();
        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}