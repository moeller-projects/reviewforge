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
    public async Task Missing_executable_fails_instead_of_throwing()
    {
        var dir = TempDir();
        try
        {
            var verifier = new ProcessFixVerifier("reviewforge-definitely-missing-binary", timeoutSeconds: 30);
            var verdict = await verifier.VerifyAsync(dir, "any.cs", CancellationToken.None);
            Assert.False(verdict.Passed);
            Assert.Contains("start", verdict.Reason, StringComparison.OrdinalIgnoreCase);
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
}
