using System.Diagnostics;
using System.Text;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Process;

/// <summary>Runs argv directly through <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public const int MaxOutputCharacters = 64 * 1024;

    public async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            throw new ArgumentException("argv must contain a non-empty executable path.", nameof(argv));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The process timeout must be positive.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{argv[0]}'.");
        }

        var outputBudget = new OutputBudget(MaxOutputCharacters);
        var stdout = CaptureAsync(process.StandardOutput, outputBudget);
        var stderr = CaptureAsync(process.StandardError, outputBudget);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            Kill(process);
        }
        catch
        {
            Kill(process);
            await AwaitOutputAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        if (timedOut)
        {
            Kill(process);
        }

        var capturedOutput = await AwaitOutputAsync(stdout, stderr).ConfigureAwait(false);
        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode,
            capturedOutput.StandardOutput,
            capturedOutput.StandardError,
            timedOut);
    }

    private static async Task<string> CaptureAsync(StreamReader reader, OutputBudget budget)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }

            var keep = budget.Reserve(read);
            if (keep > 0)
            {
                builder.Append(buffer, 0, keep);
            }
        }
    }

    private static async Task<(string StandardOutput, string StandardError)> AwaitOutputAsync(
        Task<string> stdout,
        Task<string> stderr)
    {
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return (stdout.Result, stderr.Result);
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private sealed class OutputBudget(int maximum)
    {
        private int _remaining = maximum;

        public int Reserve(int requested)
        {
            while (true)
            {
                var current = Volatile.Read(ref _remaining);
                if (current == 0)
                {
                    return 0;
                }

                var granted = Math.Min(current, requested);
                if (Interlocked.CompareExchange(ref _remaining, current - granted, current) == current)
                {
                    return granted;
                }
            }
        }
    }
}
