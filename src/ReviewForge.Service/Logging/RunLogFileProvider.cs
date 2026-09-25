using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReviewForge.Service.Logging;

public sealed class RunLogOptions
{
    public bool Enabled { get; init; } = true;
    public string DirectoryName { get; init; } = "logs"; // under ReviewForge:WorkDir
    public LogLevel MinLevel { get; init; } = LogLevel.Information; // Debug is opt-in via RunLogs:MinLevel
}

/// <summary>Lifetime hook for per-run log files; the worker closes a run's writer when the
/// run ends. Injected so the worker never touches a static provider reference (P2-31).
/// When run logs are disabled a no-op implementation is registered.</summary>
public interface IRunLogLifecycle
{
    void CloseRun(Guid runId);
}

/// <summary>No-op lifecycle used when RunLogs:Enabled=false.</summary>
internal sealed class NoopRunLogLifecycle : IRunLogLifecycle
{
    public void CloseRun(Guid runId)
    {
    }
}

/// <summary>
/// Writes one JSONL file per review run under {WorkDir}/logs/{runId:N}.jsonl.
/// Entries are routed by the RunId in the current BeginScope chain; unscoped entries
/// are ignored. The worker closes a run's writer when the run ends.
/// Writes are buffered (64 KB) and flushed by a 2 s timer plus on CloseRun; run logs are
/// best-effort diagnostics, so disposal races are swallowed rather than thrown (P2-31).
/// </summary>
public sealed class RunLogFileProvider : ILoggerProvider, ISupportExternalScope, IRunLogLifecycle
{
    /// <summary>Bound for the closed-run id set; oldest-ish entries are evicted past this.</summary>
    internal const int ClosedRunCap = 10_000;

    private readonly string _Directory;
    private readonly LogLevel _MinLevel;
    private readonly ConcurrentDictionary<Guid, RunLogWriter> _Writers = new();
    private readonly ConcurrentDictionary<Guid, byte> _ClosedRuns = new();
    private readonly Timer _FlushTimer;

    /// <summary>Writer construction seam: tests substitute a capturing factory to exercise
    /// disposal-race handling deterministically.</summary>
    internal Func<string, RunLogWriter> WriterFactory { get; set; } = path => new RunLogWriter(path);

    private IExternalScopeProvider _Scopes = new LoggerExternalScopeProvider();
    private volatile bool _Disposed;

    public RunLogFileProvider(string workDir, RunLogOptions options)
    {
        _Directory = Path.Combine(workDir, options.DirectoryName);
        _MinLevel = options.MinLevel;
        Directory.CreateDirectory(_Directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _FlushTimer = new Timer(_ => FlushAll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
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
        if (_Disposed || _ClosedRuns.ContainsKey(runId))
        {
            return; // post-close and post-dispose writes are dropped, never re-created
        }

        var writer = _Writers.GetOrAdd(runId, id => WriterFactory(Path.Combine(_Directory, $"{id:N}.jsonl")));
        try
        {
            writer.Write(new RunLogEntry(
                DateTimeOffset.UtcNow, level, category, eventId.Id, message,
                exception?.ToString(), properties));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Race with CloseRun/Dispose — run logs are best-effort diagnostics (P2-31).
        }
    }

    private void FlushAll()
    {
        foreach (var writer in _Writers.Values)
        {
            writer.Flush();
        }
    }

    /// <summary>Called by ReviewWorker in its finally block.</summary>
    public void CloseRun(Guid runId)
    {
        _ClosedRuns.TryAdd(runId, 0);
        if (_ClosedRuns.Count > ClosedRunCap)
        {
            // Bound the set: evict down to half so this is amortized O(cap/2).
            foreach (var key in _ClosedRuns.Keys)
            {
                if (_ClosedRuns.Count <= ClosedRunCap / 2)
                {
                    break;
                }

                _ClosedRuns.TryRemove(key, out _);
            }
        }

        if (_Writers.TryRemove(runId, out var writer))
        {
            writer.Dispose();
        }
    }

    /// <summary>Closed-set size, exposed for the bounding test.</summary>
    internal int ClosedRunCount => _ClosedRuns.Count;

    public void Dispose()
    {
        _Disposed = true;
        _FlushTimer.Dispose();
        foreach (var writer in _Writers.Values)
        {
            writer.Dispose();
        }

        _Writers.Clear();
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
    private const int BufferSize = 64 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamWriter _Writer;
    private readonly object _Gate = new();
    private bool _Disposed;

    public RunLogWriter(string path)
        : this(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), setFileMode: true, path)
    {
    }

    /// <summary>Stream seam for tests: IO failures on Flush/Dispose simulate disk errors.</summary>
    internal RunLogWriter(Stream stream)
        : this(stream, setFileMode: false, path: null)
    {
    }

    private RunLogWriter(Stream stream, bool setFileMode, string? path)
    {
        _Writer = new StreamWriter(stream, Utf8NoBom, BufferSize)
        {
            AutoFlush = false, // buffered; flushed by the provider timer and on Dispose (P2-31)
        };
        if (setFileMode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path!, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Write(RunLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        lock (_Gate)
        {
            _Writer.WriteLine(line);
        }
    }

    public void Flush()
    {
        lock (_Gate)
        {
            if (!_Disposed)
            {
                try
                {
                    _Writer.Flush();
                }
                catch (IOException)
                {
                    // Best-effort diagnostics (P2-31): never throw out of the timer thread.
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_Gate)
        {
            if (_Disposed)
            {
                return;
            }

            try
            {
                _Writer.Flush();
            }
            catch (IOException)
            {
                // Best-effort diagnostics (P2-31): the buffered tail may be lost on IO error.
            }

            try
            {
                // StreamWriter.Dispose flushes again; an IO error there must not escape either.
                _Writer.Dispose();
            }
            catch (IOException)
            {
            }

            _Disposed = true;
        }
    }
}

public sealed record RunLogEntry(
    DateTimeOffset Timestamp, LogLevel Level, string Category, int EventId,
    string Message, string? Exception, IReadOnlyDictionary<string, object?> Properties);
