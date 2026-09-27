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
}
