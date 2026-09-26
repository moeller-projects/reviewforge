using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using ReviewForge.Core.AutoFix;

namespace ReviewForge.Infrastructure.AutoFix;

/// <summary>
/// Runs <c>AutoFix:VerificationCommand</c> in the checkout to verify fixed files. No
/// shell: executable + <see cref="ProcessStartInfo.ArgumentList"/>, working directory
/// pinned to the checkout, stdout/stderr bounded to 64 KB each, wall-clock timeout with
/// kill(entireProcessTree: true). Exit 0 = pass; anything else fails with the stderr tail.
///
/// SECURITY: the command executes PR-author-controlled code (dotnet build runs MSBuild
/// targets; npm test runs scripts). Enable only with a strict AutoFix author allowlist.
/// The shipped container (Alpine, read-only rootfs, no toolchains) cannot run heavyweight
/// verifiers.
/// </summary>
[ExcludeFromCodeCoverage] // pure process wrapper; command parsing/validation lives in covered Core code
public sealed class ProcessFixVerifier : IFixVerifier
{
    public const int MaxOutputBytes = 64 * 1024;

    private readonly string _Executable;
    private readonly string[] _Arguments;
    private readonly int _TimeoutSeconds;

    public ProcessFixVerifier(string command, int timeoutSeconds)
    {
        (_Executable, _Arguments) = ProcessFixCommand.Parse(command);
        _TimeoutSeconds = timeoutSeconds;
    }

    public string Name => "process";
    public bool RequiresWorkspaceWrites => true;

    public async Task<FixVerdict> VerifyAsync(string repoDir, string relativeFilePath, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_TimeoutSeconds));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _Executable,
                WorkingDirectory = repoDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in _Arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendBounded(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendBounded(stderr, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new FixVerdict(false, $"verification process failed to start: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillTree(process);
            return new FixVerdict(false, $"verification timed out after {_TimeoutSeconds}s");
        }

        // WaitForExitAsync may return before async output drains; give it a moment.
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return new FixVerdict(true, "exit 0");
        }

        var tail = Tail(stderr);
        return new FixVerdict(false, string.IsNullOrWhiteSpace(tail) ? $"exit {process.ExitCode}" : $"exit {process.ExitCode}: {tail}");
    }

    private static void AppendBounded(StringBuilder sb, string? data)
    {
        if (data is null || sb.Length >= MaxOutputBytes)
        {
            return;
        }

        sb.AppendLine(data.Length > MaxOutputBytes ? data[..MaxOutputBytes] : data);
    }

    private static string Tail(StringBuilder stderr)
    {
        var text = stderr.ToString();
        const int max = 2000;
        return text.Length <= max ? text.Trim() : text[^max..].Trim();
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }
}
