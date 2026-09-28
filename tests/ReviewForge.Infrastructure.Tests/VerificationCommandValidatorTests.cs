using ReviewForge.Core.AutoFix;
using ReviewForge.Infrastructure.AutoFix;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class VerificationCommandValidatorTests
{
    private readonly VerificationCommandValidator _Validator = new();

    [Fact]
    public void Empty_command_is_valid()
    {
        var result = _Validator.Validate(null, new AutoFixOptions { VerificationCommand = " " });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Invalid_command_is_rejected()
    {
        var result = _Validator.Validate(null, new AutoFixOptions { VerificationCommand = "echo && whoami" });

        Assert.False(result.Succeeded);
        Assert.Contains("invalid", result.Failures!.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_executable_is_rejected()
    {
        var result = _Validator.Validate(null, new AutoFixOptions
        {
            VerificationCommand = "reviewforge-executable-that-does-not-exist-9f1c"
        });

        Assert.False(result.Succeeded);
        Assert.Contains("was not found", result.Failures!.Single());
    }

    [Fact]
    public void Existing_absolute_executable_is_valid()
    {
        var executable = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(executable));

        var result = _Validator.Validate(null, new AutoFixOptions
        {
            VerificationCommand = $"{executable}"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Exists_finds_an_executable_by_exact_name_on_the_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var name = OperatingSystem.IsWindows() ? "rf-marker.cmd" : "rf-marker";
        File.WriteAllText(Path.Combine(dir, name), OperatingSystem.IsWindows() ? "@echo off" : "#!/bin/sh");

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + original);

            Assert.True(ExecutableResolver.Exists(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
            File.Delete(Path.Combine(dir, name));
            Directory.Delete(dir);
        }
    }
}
