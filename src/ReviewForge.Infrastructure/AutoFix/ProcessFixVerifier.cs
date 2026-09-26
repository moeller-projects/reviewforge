using System.ComponentModel;
using System.Diagnostics;
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
public sealed class ProcessFixVerifier : IFixVerifier
{
    public const int MaxOutputBytes = 64 * 1024;

    private static readonly int LineTerminatorBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);

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
        var stdoutBytes = 0;
        var stderrBytes = 0;
        process.OutputDataReceived += (_, e) => AppendBounded(stdout, ref stdoutBytes, e.Data);
        process.ErrorDataReceived += (_, e) => AppendBounded(stderr, ref stderrBytes, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            Reap(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

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

    private static void AppendBounded(StringBuilder sb, ref int retainedBytes, string? data)
    {
        if (data is null)
        {
            return;
        }

        var available = MaxOutputBytes - retainedBytes;
        if (available < LineTerminatorBytes)
        {
            return;
        }

        var payloadBudget = available - LineTerminatorBytes;
        var payloadBytes = Encoding.UTF8.GetByteCount(data);
        if (payloadBytes <= payloadBudget)
        {
            sb.Append(data);
            retainedBytes += payloadBytes;
        }
        else
        {
            var usedBytes = 0;
            for (var offset = 0; offset < data.Length;)
            {
                var charCount = char.IsHighSurrogate(data[offset])
                    && offset + 1 < data.Length
                    && char.IsLowSurrogate(data[offset + 1])
                    ? 2
                    : 1;
                var charBytes = Encoding.UTF8.GetByteCount(data.AsSpan(offset, charCount));
                if (usedBytes + charBytes > payloadBudget)
                {
                    break;
                }

                sb.Append(data, offset, charCount);
                usedBytes += charBytes;
                offset += charCount;
            }

            retainedBytes += usedBytes;
        }

        sb.Append(Environment.NewLine);
        retainedBytes += LineTerminatorBytes;
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
        catch (Win32Exception)
        {
            // process exited between the cancellation and kill attempt
        }
    }

    private static void Reap(Process process)
    {
        try
        {
            if (process.WaitForExit(milliseconds: 1000))
            {
                // Synchronous WaitForExit drains redirected output event handlers.
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // process was not started or has already been disposed
        }
    }
}
