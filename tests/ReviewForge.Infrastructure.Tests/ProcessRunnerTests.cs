using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Process;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Runs_executable_with_argument_and_returns_exit_result()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            [RuntimeExecutable(), "--version"],
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEmpty(result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Captures_stdout_and_stderr_and_preserves_nonzero_exit_code()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            OutputAndFailureCommand(),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("standard-output", result.StandardOutput);
        Assert.Equal("standard-error", result.StandardError);
    }

    [Fact]
    public async Task Caps_combined_output_without_timing_out()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            LargeOutputCommand(),
            workingDirectory: null,
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ProcessRunner.MaxOutputCharacters, result.StandardOutput.Length + result.StandardError.Length);
    }

    private static string RuntimeExecutable() => "dotnet";

    private static IReadOnlyList<string> OutputAndFailureCommand() =>
        OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", "<nul set /p=standard-output&1>&2 <nul set /p=standard-error&exit /b 7"]
            : ["/bin/sh", "-c", "printf 'standard-output'; printf 'standard-error' >&2; exit 7"];

    private static IReadOnlyList<string> LargeOutputCommand() =>
        OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", "for /L %i in (1,1,70000) do @echo x"]
            : ["/bin/sh", "-c", "yes x | head -c 70000"];

    [Fact]
    public async Task Rejects_invalid_arguments_and_timeout()
    {
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            runner.RunAsync(null!, null, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync([], null, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            runner.RunAsync(["dotnet"], null, TimeSpan.Zero));
    }

    [Fact]
    public async Task Propagates_caller_cancellation_after_killing_the_process()
    {
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var execution = runner.RunAsync(BlockingCommand(), null, TimeSpan.FromSeconds(20), cancellation.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }

    [Fact]
    public async Task Reports_timeout_and_kills_the_process()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            BlockingCommand(), null, TimeSpan.FromMilliseconds(250));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Executes_in_the_requested_working_directory()
    {
        var runner = new ProcessRunner();
        var workingDirectory = Path.GetFullPath(Path.GetTempPath());
        var result = await runner.RunAsync(
            CurrentDirectoryCommand(), workingDirectory, TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded);
        Assert.True(string.Equals(workingDirectory.TrimEnd(Path.DirectorySeparatorChar),
            result.StandardOutput.Trim().TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    [Fact]
    public async Task Propagates_process_start_failure()
    {
        var runner = new ProcessRunner();
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            runner.RunAsync(["reviewforge-missing-process-74c4"], null, TimeSpan.FromSeconds(1)));
    }

    private static IReadOnlyList<string> BlockingCommand() =>
        OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", "ping -n 30 127.0.0.1 > nul"]
            : ["/bin/sh", "-c", "sleep 10"];

    private static IReadOnlyList<string> CurrentDirectoryCommand() =>
        OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", "cd"]
            : ["/bin/pwd"];
}
