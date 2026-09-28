using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using ReviewForge.Infrastructure.AutoFix;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class ProcessFixVerifierTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reviewforge-verifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Exit_zero_passes()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("true", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);
            Assert.True(verdict.Passed);
            Assert.Equal("process", verifier.Name);
            Assert.True(verifier.RequiresWorkspaceWrites);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Nonzero_exit_fails_with_bounded_output()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("false", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);
            Assert.False(verdict.Passed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Child_process_does_not_inherit_service_environment()
    {
        // The verifier executes PR-controlled code; service secrets must not reach it.
        var dir = TempDir();
        var script = Path.Combine(dir, "check-env.sh");
        await File.WriteAllTextAsync(script,
            "#!/bin/sh\nif [ -n \"$REVIEWFORGE_TEST_SECRET\" ]; then echo leaked >&2; exit 1; fi\nexit 0\n");
        var previous = Environment.GetEnvironmentVariable("REVIEWFORGE_TEST_SECRET");
        Environment.SetEnvironmentVariable("REVIEWFORGE_TEST_SECRET", "hunter2");
        try
        {
            var verifier = new ProcessFixVerifier($"sh {script}", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);

            Assert.True(verdict.Passed, verdict.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REVIEWFORGE_TEST_SECRET", previous);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_executable_propagates_start_failure()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("reviewforge-definitely-missing-binary", timeoutSeconds: 30);

            await Assert.ThrowsAsync<Win32Exception>(
                () => verifier.VerifyAsync(dir, "any.cs", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_kills_process_and_rethrows()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("sleep 30", timeoutSeconds: 30);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var stopwatch = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verifier.VerifyAsync(dir, "any.cs", cts.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Output_bound_counts_utf8_bytes_and_truncates_final_line()
    {
        var dir = TempDir();
        var script = Path.Combine(dir, "emit-output.sh");
        try
        {
            var output = new string('é', ProcessFixVerifier.MaxOutputBytes);
            await File.WriteAllTextAsync(
                script,
                $"#!/bin/sh\nprintf '%s\\n' '{output}' >&2\nexit 1\n");

            var verifier = new ProcessFixVerifier($"sh {script}", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);

            Assert.False(verdict.Passed);
            Assert.StartsWith("exit 1:", verdict.Reason, StringComparison.Ordinal);
            Assert.True(verdict.Reason.Length <= 2010);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Timed_out_process_fails()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("sleep 30", timeoutSeconds: 1);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);
            Assert.False(verdict.Passed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_placeholder_runs_once_per_edited_file()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("printf {file}", timeoutSeconds: 30);

            var verdict = await verifier.VerifyAsync(dir, ["one.cs", "two.cs"], CancellationToken.None);

            Assert.True(verdict.Passed);
            Assert.Equal("verified 2 file(s)", verdict.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_placeholder_reports_the_file_that_failed()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("false {file}", timeoutSeconds: 30);

            var verdict = await verifier.VerifyAsync(dir, ["broken.cs"], CancellationToken.None);

            Assert.False(verdict.Passed);
            Assert.Contains("broken.cs:", verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_placeholder_rejects_more_than_the_maximum_file_count()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("true {file}", timeoutSeconds: 30);

            var verdict = await verifier.VerifyAsync(
                dir,
                Enumerable.Range(0, 33).Select(i => $"file-{i}.cs").ToArray(),
                CancellationToken.None);

            Assert.False(verdict.Passed);
            Assert.Contains("at most 32 edited files", verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Bounded_output_ignores_lines_after_byte_budget()
    {
        var dir = TempDir();
        var script = Path.Combine(dir, "emit-two-lines.sh");
        try
        {
            var output = new string('é', ProcessFixVerifier.MaxOutputBytes);
            await File.WriteAllTextAsync(
                script,
                $"#!/bin/sh\nprintf '%s\\n' '{output}' 'ignored' 'ignored-too' >&2\nexit 1\n");

            var verifier = new ProcessFixVerifier($"sh {script}", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);

            Assert.False(verdict.Passed);
            Assert.DoesNotContain("ignored", verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Short_output_is_reported_without_truncation()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("printf verifier-output", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);

            Assert.True(verdict.Passed);
            Assert.Equal("exit 0", verdict.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Defensive_kill_and_reap_paths_ignore_unstarted_processes()
    {
        using var process = new Process();
        InvokePrivate("TryKillTree", process);
        InvokePrivate("Reap", process);
    }

    [Fact]
    public void Defensive_kill_path_ignores_native_kill_failure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var process = Process.GetProcessById(1);
        InvokePrivate("TryKillTree", process);
    }

    private static void InvokePrivate(string name, Process process)
    {
        var method = typeof(ProcessFixVerifier).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing private method {name}");
        method.Invoke(null, [process]);
    }

}
