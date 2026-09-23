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

        RunLogFileProvider.Current?.Dispose();
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
    public void CloseRun_then_log_reopens_in_append_mode()
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
        provider.CloseRun(runId);

        var file = Assert.Single(Directory.GetFiles(LogsDir));
        Assert.Equal(2, File.ReadLines(file).Count());
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
}
