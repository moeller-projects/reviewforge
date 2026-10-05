namespace ReviewForge.Core.Ports;

/// <summary>Runs a configured executable and argument vector without invoking a shell.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="argv"/>, where the first element is the executable and remaining
    /// elements are arguments. Standard output and error are capped at 64 KiB combined.
    /// A timeout returns a timed-out result; caller cancellation propagates as cancellation.
    /// </summary>
    Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>The bounded result of one configured process invocation.</summary>
public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}
