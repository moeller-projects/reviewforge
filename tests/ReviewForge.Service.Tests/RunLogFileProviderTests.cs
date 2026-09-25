using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReviewForge.Service.Logging;
using Xunit;

namespace ReviewForge.Service.Tests;

public class RunLogFileProviderTests : IDisposable
{
    private readonly string _WorkDir = Path.Combine(Path.GetTempPath(), "reviewforge-runlog-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _Disposables = [];

    private ILogger BuildLogger(out RunLogFileProvider provider, RunLogOptions? options = null)
    {
        var p = new RunLogFileProvider(_WorkDir, options ?? new RunLogOptions());
        provider = p;
        var factory = LoggerFactory.Create(b => b.AddProvider(p));
        _Disposables.Add(factory);
        return factory.CreateLogger("test");
    }

    public void Dispose()
    {
        foreach (var d in _Disposables)
        {
            d.Dispose();
        }

        if (Directory.Exists(_WorkDir))
        {
            try
            {
                Directory.Delete(_WorkDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string LogsDir => Path.Combine(_WorkDir, "logs");

    [Fact]
    public void Unscoped_entry_writes_nothing()
    {
        var logger = BuildLogger(out _);
        logger.LogInformation("unscoped");
        Assert.Empty(Directory.GetFiles(LogsDir));
    }

    [Fact]
    public void Scoped_entry_writes_one_json_line()
    {
        var logger = BuildLogger(out var provider);
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            logger.LogInformation("stage {Stage} done", "x");
        }
        provider.CloseRun(runId);

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Equal($"{runId:N}.jsonl", Path.GetFileName(file));
        var line = Assert.Single(File.ReadLines(file));
        var entry = JsonSerializer.Deserialize<RunLogEntry>(line)!;
        Assert.Contains("stage x done", entry.Message);
        Assert.Equal("test", entry.Category);
    }

    [Fact]
    public void CloseRun_then_log_is_dropped_and_writer_not_recreated()
    {
        var logger = BuildLogger(out var provider);
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            logger.LogInformation("first");
        }
        provider.CloseRun(runId);

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            logger.LogInformation("second");
        }

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Equal(new[] {"first"}, File.ReadLines(file).Select(l => JsonSerializer.Deserialize<RunLogEntry>(l)!.Message));
    }

    [Fact]
    public void Buffered_entries_are_all_flushed_by_CloseRun()
    {
        var logger = BuildLogger(out var provider);
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            for (var i = 0; i < 1000; i++)
            {
                logger.LogInformation("entry {Index}", i);
            }
        }
        provider.CloseRun(runId);

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        var lines = File.ReadLines(file).ToList();
        Assert.Equal(1000, lines.Count);
        Assert.Contains("entry 999", lines[^1]);
    }

    [Fact]
    public async Task Write_racing_Dispose_throws_nothing()
    {
        var provider = new RunLogFileProvider(_WorkDir, new RunLogOptions());
        var runId = Guid.NewGuid();
        var writers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 500; i++)
                {
                    provider.Write(runId, LogLevel.Information, "t", new EventId(1), "m", null, new Dictionary<string, object?>());
                }
            }))
            .ToList();

        var dispose = Task.Run(() => provider.Dispose());
        await Task.WhenAll(writers.Append(dispose));
    }

    [Fact]
    public void Write_after_writer_disposal_is_swallowed()
    {
        var provider = new RunLogFileProvider(_WorkDir, new RunLogOptions());
        RunLogWriter? captured = null;
        provider.WriterFactory = path =>
        {
            captured = new RunLogWriter(path);
            return captured;
        };
        var runId = Guid.NewGuid();

        provider.Write(runId, LogLevel.Information, "t", new EventId(1), "before", null, new Dictionary<string, object?>());
        captured!.Dispose(); // simulates CloseRun/Dispose winning the race mid-flight
        provider.Write(runId, LogLevel.Information, "t", new EventId(1), "after", null, new Dictionary<string, object?>());

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Contains("before", File.ReadAllText(file));
        provider.Dispose();
    }

    [Fact]
    public void Write_after_provider_Dispose_is_dropped()
    {
        var provider = new RunLogFileProvider(_WorkDir, new RunLogOptions());
        provider.Dispose();
        provider.Write(Guid.NewGuid(), LogLevel.Information, "t", new EventId(1), "late", null, new Dictionary<string, object?>());
        Assert.Empty(Directory.GetFiles(LogsDir));
    }

    [Fact]
    public void Closed_run_set_is_bounded()
    {
        var provider = new RunLogFileProvider(_WorkDir, new RunLogOptions());
        for (var i = 0; i < RunLogFileProvider.ClosedRunCap + 1; i++)
        {
            provider.CloseRun(Guid.NewGuid());
        }

        Assert.True(provider.ClosedRunCount <= RunLogFileProvider.ClosedRunCap);
        provider.Dispose();
    }

    [Fact]
    public void Unix_file_and_directory_modes_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix modes do not apply
        }

        var logger = BuildLogger(out var provider);
        var runId = Guid.NewGuid();
        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            logger.LogInformation("x");
        }
        provider.CloseRun(runId);

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(LogsDir));
    }

    [Fact]
    public void MinLevel_defaults_to_information()
    {
        Assert.Equal(LogLevel.Information, new RunLogOptions().MinLevel);
    }

    [Fact]
    public void MinLevel_filters_debug_entries()
    {
        var logger = BuildLogger(out var provider, new RunLogOptions {MinLevel = LogLevel.Information});
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId}))
        {
            logger.LogDebug("debug detail");
            logger.LogInformation("info detail");
        }
        provider.CloseRun(runId);

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        var text = File.ReadAllText(file);
        Assert.Contains("info detail", text);
        Assert.DoesNotContain("debug detail", text);
    }

    [Fact]
    public void Scoped_entry_accepts_a_string_run_id()
    {
        var logger = BuildLogger(out _);
        var runId = Guid.NewGuid();

        using (logger.BeginScope(new Dictionary<string, object> {["RunId"] = runId.ToString("N")}))
        {
            logger.LogInformation("in run");
        }

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Equal($"{runId:N}.jsonl", Path.GetFileName(file));
    }

    [Fact]
    public void RunLogger_scopes_are_noops()
    {
        var provider = new RunLogFileProvider(_WorkDir, new RunLogOptions());
        try
        {
            var logger = provider.CreateLogger("t");
            Assert.Null(logger.BeginScope(("k", "v")));
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public void Noop_lifecycle_close_is_safe()
    {
        IRunLogLifecycle lifecycle = new NoopRunLogLifecycle();
        lifecycle.CloseRun(Guid.NewGuid());
    }

    [Fact]
    public void Writer_flush_and_dispose_swallow_io_errors()
    {
        var entry = new RunLogEntry(DateTimeOffset.UtcNow, LogLevel.Information, "t", 1, "m", null, new Dictionary<string, object?>());
        var failing = new RunLogWriter(new ThrowOnFlushStream());
        failing.Write(entry);
        failing.Flush();
        failing.Dispose();

        var healthy = new RunLogWriter(new MemoryStream());
        healthy.Write(entry);
        healthy.Flush();
        healthy.Dispose();
    }

    /// <summary>Simulates a stream whose final flush fails (e.g. disk full).</summary>
    private sealed class ThrowOnFlushStream : MemoryStream
    {
        public override void Flush() => throw new IOException("simulated flush failure");
    }
}
