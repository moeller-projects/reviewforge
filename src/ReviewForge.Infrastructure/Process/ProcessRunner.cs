using System.Diagnostics;
using System.Text;
using ReviewForge.Core.Ports;

namespace ReviewForge.Infrastructure.Process;

/// <summary>Runs argv directly through <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Combined stdout+stderr cap in UTF-8 BYTES (the port promises 64 KiB; a
    /// char-counted cap would let non-ASCII output retain up to 4x that).</summary>
    public const int MaxOutputBytes = 64 * 1024;

    /// <summary>Operational variables a build tool legitimately needs. The child process runs
    /// repository-controlled build hooks on the PR checkout, so it must NOT inherit the
    /// service environment (ADO PAT, model-provider keys, webhook secrets): the environment is
    /// cleared and only this allowlist is passed through.</summary>
    public static readonly IReadOnlyList<string> EnvironmentAllowlist =
    [
        // Executable resolution and shell plumbing.
        "PATH", "Path", "PATHEXT", "COMSPEC", "SystemRoot", "SYSTEMROOT", "OS",
        // Temp and profile locations build tools write caches to.
        "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "ProgramFiles(x86)",
        // .NET / NuGet behavior knobs (non-secret).
        "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "DOTNET_CLI_UI_LANGUAGE",
        "DOTNET_MULTILEVEL_LOOKUP", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH",
        "NUGET_FALLBACK_PACKAGES", "NUMBER_OF_PROCESSORS",
        // Locale.
        "LANG", "LC_ALL", "TZ",
    ];

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

        // Secret hygiene: never inherit the service environment — pass through only the
        // operational allowlist (see EnvironmentAllowlist).
        startInfo.Environment.Clear();
        foreach (var name in EnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }
        for (var i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{argv[0]}'.");
        }

        var outputBudget = new OutputBudget(MaxOutputBytes);
        var stdout = CaptureAsync(process.StandardOutput, outputBudget);
        var stderr = CaptureAsync(process.StandardError, outputBudget);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            // The drain shares the same budget: a DESCENDANT that inherited stdout/stderr
            // keeps the pipes open after the direct child exits — WaitForExitAsync alone
            // would let that shape hang the run past its timeout.
            await Task.WhenAll(stdout, stderr).WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch
        {
            Kill(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        if (timedOut)
        {
            // Tree kill also terminates pipe-holding descendants, so the readers complete.
            Kill(process);
            await DrainAfterKillAsync(stdout, stderr).ConfigureAwait(false);
        }

        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode,
            stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
            stderr.IsCompletedSuccessfully ? stderr.Result : string.Empty,
            timedOut);
    }

    /// <summary>Bounded grace drain after a tree kill: the pipes close with the dead
    /// processes and the readers complete. If they still do not (a reader itself faulted),
    /// return what was captured rather than hang the run — the primary outcome (timeout or
    /// the original exception) is already decided.</summary>
    private static async Task DrainAfterKillAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Readers never completed even after the tree kill — proceed with empty output.
        }
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

            var keep = budget.Reserve(Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read)));
            if (keep > 0)
            {
                builder.Append(buffer, 0, CharsFittingByteBudget(buffer.AsSpan(0, read), keep));
            }
        }
    }

    /// <summary>Longest char prefix of <paramref name="chunk"/> whose UTF-8 encoding fits in
    /// <paramref name="byteBudget"/>. Only runs on the single truncated chunk — the steady
    /// state appends whole buffers.</summary>
    private static int CharsFittingByteBudget(ReadOnlySpan<char> chunk, int byteBudget)
    {
        var chars = 0;
        var bytes = 0;
        while (chars < chunk.Length)
        {
            var width = chars + 1 < chunk.Length && char.IsSurrogatePair(chunk[chars], chunk[chars + 1]) ? 2 : 1;
            var cost = Encoding.UTF8.GetByteCount(chunk.Slice(chars, width));
            if (bytes + cost > byteBudget)
            {
                break;
            }

            bytes += cost;
            chars += width;
        }

        return chars;
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
