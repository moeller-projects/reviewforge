using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ReviewForge.Service;

/// <summary>Writes compact, single-line console logs with multiline exceptions.</summary>
internal sealed class CompactConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "compact";

    private readonly IOptionsMonitor<ConsoleFormatterOptions> _OptionsMonitor;

    public CompactConsoleFormatter(IOptionsMonitor<ConsoleFormatterOptions> optionsMonitor)
        : base(FormatterName)
    {
        _OptionsMonitor = optionsMonitor;
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var options = _OptionsMonitor.CurrentValue;
        var level = Abbreviate(logEntry.LogLevel);
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var source = LastNamespaceEntry(logEntry.Category);
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

        textWriter.Write(level);
        textWriter.Write(' ');
        textWriter.Write(timestamp);
        textWriter.Write(' ');
        textWriter.Write(source);
        textWriter.Write(": ");
        WriteSingleLine(textWriter, message);

        if (options.IncludeScopes && scopeProvider is not null)
        {
            scopeProvider.ForEachScope(static (scope, writer) =>
            {
                writer.Write(" [");
                writer.Write(scope);
                writer.Write(']');
            }, textWriter);
        }

        if (logEntry.Exception is not null)
        {
            textWriter.WriteLine();
            textWriter.Write(logEntry.Exception);
        }

        textWriter.WriteLine();
    }

    private static void WriteSingleLine(TextWriter writer, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        writer.Write(value.ReplaceLineEndings(" "));
    }

    private static string LastNamespaceEntry(string category)
    {
        var separator = category.LastIndexOf('.');
        return separator >= 0 && separator < category.Length - 1
            ? category[(separator + 1)..]
            : category;
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON",
    };
}