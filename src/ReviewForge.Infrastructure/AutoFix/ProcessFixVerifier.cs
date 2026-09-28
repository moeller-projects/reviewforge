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
/// The child process gets a deny-by-default environment (PATH/HOME/TMPDIR and dotnet
/// conveniences only) so service secrets cannot be inherited. The shipped container
/// (Alpine, read-only rootfs, no toolchains) cannot run heavyweight verifiers.
public sealed class ProcessFixVerifier : IFixVerifier
{
    public const int MaxOutputBytes = 64 * 1024;


    private readonly string _Executable;
    private readonly string[] _Arguments;
    private readonly int _TimeoutSeconds;
    private const int MaxFileInvocations = 32;

    public ProcessFixVerifier(string command, int timeoutSeconds)
    {
        var parsed = ProcessFixCommand.Parse(command);
        _Executable = parsed.Executable;
        _Arguments = parsed.Arguments;
        _TimeoutSeconds = timeoutSeconds;
    }

    public string Name => "process";
    public bool RequiresWorkspaceWrites => true;
    public Task<FixVerdict> VerifyAsync(
        string repoDir, string relativeFilePath, CancellationToken ct)
        => VerifyAsync(repoDir, new[] { relativeFilePath }, ct);

    public async Task<FixVerdict> VerifyAsync(
        string repoDir, IReadOnlyList<string> editedFiles, CancellationToken ct)
    {
        if (!_Arguments.Contains(ProcessFixCommand.FilePlaceholder, StringComparer.Ordinal))
        {
            return await RunOnceAsync(repoDir, _Arguments, ct).ConfigureAwait(false);
        }

        if (editedFiles.Count > MaxFileInvocations)
        {
            return new FixVerdict(
                false,
                $"verification supports at most {MaxFileInvocations} edited files when {ProcessFixCommand.FilePlaceholder} is used");
        }

        var failures = new List<string>();
        foreach (var file in editedFiles)
        {
            var arguments = _Arguments
                .Select(a => a == ProcessFixCommand.FilePlaceholder ? file : a)
                .ToArray();
            var verdict = await RunOnceAsync(repoDir, arguments, ct).ConfigureAwait(false);
            if (!verdict.Passed)
            {
                failures.Add($"{file}: {verdict.Reason}");
            }
        }

        return failures.Count == 0
            ? new FixVerdict(true, $"verified {editedFiles.Count} file(s)")
            : new FixVerdict(false, $"verification failed for {string.Join("; ", failures)}");
    }

    private async Task<FixVerdict> RunOnceAsync(
        string repoDir, IReadOnlyList<string> arguments, CancellationToken ct)
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
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        // Deny-by-default environment: the verifier executes PR-controlled code
        // (MSBuild targets, npm scripts) and must never see service secrets
        // (REVIEWFORGE_ADO_PAT, REVIEWFORGE_API_KEYS, OPENAI_API_KEY).
        process.StartInfo.Environment.Clear();
        foreach (var keep in new[]
                 {
                     "PATH", "HOME", "TMPDIR", "DOTNET_ROOT",
                     "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "NUGET_PACKAGES",
                 })
        {
            if (Environment.GetEnvironmentVariable(keep) is { } value)
            {
                process.StartInfo.Environment[keep] = value;
            }
        }

        process.Start();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            Reap(process);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new FixVerdict(false, $"verification timed out after {_TimeoutSeconds}s");
        }

        var output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return new FixVerdict(true, "exit 0");
        }

        var tail = Tail(Encoding.UTF8.GetString(output[1]));
        return new FixVerdict(false, string.IsNullOrWhiteSpace(tail)
            ? $"exit {process.ExitCode}"
            : $"exit {process.ExitCode}: {tail}");
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var retained = new MemoryStream(capacity: MaxOutputBytes);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                var remaining = MaxOutputBytes - (int)retained.Length;
                if (remaining > 0)
                {
                    retained.Write(buffer, 0, Math.Min(read, remaining));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Preserve the bounded prefix while the process is being terminated.
        }

        return retained.ToArray();
    }


    private static string Tail(string text)
    {
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
