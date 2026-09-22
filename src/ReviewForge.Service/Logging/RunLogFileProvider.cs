using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewForge.Service.Logging;

public sealed class RunLogOptions
{
    public bool Enabled { get; init; } = true;
    public string DirectoryName { get; init; } = "logs"; // under ReviewForge:WorkDir
    public LogLevel MinLevel { get; init; } = LogLevel.Debug; // files are the verbose record
}

/// <summary>
/// Writes one JSONL file per review run under {WorkDir}/logs/{runId:N}.jsonl.
/// Entries are routed by the RunId in the current BeginScope chain; unscoped entries
/// are ignored. The worker closes a run's writer when the run ends.
/// </summary>
public sealed class RunLogFileProvider : ILoggerProvider, ISupportExternalScope
{
    public static RunLogFileProvider? Current { get; private set; }

    private readonly string _Directory;
    private readonly LogLevel _MinLevel;
    private readonly ConcurrentDictionary<Guid, RunLogWriter> _Writers = new();
    private IExternalScopeProvider _Scopes = new LoggerExternalScopeProvider();

    public RunLogFileProvider(string workDir, RunLogOptions options)
    {
        _Directory = Path.Combine(workDir, options.DirectoryName);
        _MinLevel = options.MinLevel;
        Directory.CreateDirectory(_Directory);
        Current = this;
    }

    public ILogger CreateLogger(string categoryName) => new RunLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _Scopes = scopeProvider;

    internal bool TryGetRunId(out Guid runId)
    {
        var found = Guid.Empty;
        var ok = false;
        _Scopes.ForEachScope((scope, _) =>
        {
            if (ok)
            {
                return;
            }

            if (scope is IEnumerable<KeyValuePair<string, object>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key == "RunId" && pair.Value is Guid g)
                    {
                        found = g;
                        ok = true;
                        return;
                    }

                    if (pair.Key == "RunId" && Guid.TryParse(pair.Value?.ToString(), out var parsed))
                    {
                        found = parsed;
                        ok = true;
                        return;
                    }
                }
            }
        }, (object?)null);
        runId = found;
        return ok;
    }

    internal bool IsEnabled(LogLevel level) => level >= _MinLevel;

    internal void Write(Guid runId, LogLevel level, string category, EventId eventId,
        string message, Exception? exception, IReadOnlyDictionary<string, object?> properties)
    {
        var writer = _Writers.GetOrAdd(runId, id =>
            new RunLogWriter(Path.Combine(_Directory, $"{id:N}.jsonl")));
        writer.Write(new RunLogEntry(
            DateTimeOffset.UtcNow, level, category, eventId.Id, message,
            exception?.ToString(), properties));
    }

    /// <summary>Called by ReviewWorker in its finally block.</summary>
    public void CloseRun(Guid runId)
    {
        if (_Writers.TryRemove(runId, out var writer))
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var writer in _Writers.Values)
        {
            writer.Dispose();
        }

        _Writers.Clear();
        if (Current == this)
        {
            Current = null;
        }
    }
}

internal sealed class RunLogger(string category, RunLogFileProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null; // scopes flow through the provider's IExternalScopeProvider

    public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || !provider.TryGetRunId(out var runId))
        {
            return;
        }

        var properties = new Dictionary<string, object?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key != "{OriginalFormat}")
                {
                    properties[pair.Key] = pair.Value;
                }
            }
        }

        provider.Write(runId, logLevel, category, eventId, formatter(state, exception), exception, properties);
    }
}

internal sealed class RunLogWriter : IDisposable
{
    private readonly StreamWriter _Writer;
    private readonly object _Gate = new();

    public RunLogWriter(string path)
        => _Writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

    public void Write(RunLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        lock (_Gate)
        {
            _Writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_Gate)
        {
            _Writer.Dispose();
        }
    }
}

public sealed record RunLogEntry(
    DateTimeOffset Timestamp, LogLevel Level, string Category, int EventId,
    string Message, string? Exception, IReadOnlyDictionary<string, object?> Properties);
