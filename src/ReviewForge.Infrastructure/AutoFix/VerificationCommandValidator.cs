using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;

namespace ReviewForge.Infrastructure.AutoFix;

/// <summary>Validates configured verification commands before the host starts.</summary>
public sealed class VerificationCommandValidator : IValidateOptions<AutoFixOptions>
{
    public ValidateOptionsResult Validate(string? name, AutoFixOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.VerificationCommand))
        {
            return ValidateOptionsResult.Success;
        }

        if (!ProcessFixCommand.TryParse(options.VerificationCommand, out var command, out var error))
        {
            return ValidateOptionsResult.Fail($"AutoFix:VerificationCommand is invalid: {error}");
        }

        if (!ExecutableResolver.Exists(command.Executable))
        {
            return ValidateOptionsResult.Fail(
                $"AutoFix:VerificationCommand executable '{command.Executable}' was not found on PATH or as a file.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal static class ExecutableResolver
{
    public static bool Exists(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var configuredExtension = Path.GetExtension(executable);
        var hasPathext = extensions.Any(ext =>
            string.Equals(ext, configuredExtension, StringComparison.OrdinalIgnoreCase));

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable)))
            {
                return true;
            }

            if (!hasPathext)
            {
                foreach (var extension in extensions)
                {
                    if (File.Exists(Path.Combine(directory, executable + extension)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
