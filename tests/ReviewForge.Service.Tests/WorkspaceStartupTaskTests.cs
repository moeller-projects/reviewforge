using Microsoft.Extensions.Options;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class WorkspaceStartupTaskTests
{
    [Fact]
    public async Task Start_creates_workspace_and_findings_directories()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "reviewforge-startup-" + Guid.NewGuid().ToString("N"));
        var task = new WorkspaceStartupTask(Options.Create(new WorkspaceOptions { WorkDir = workDir }));

        try
        {
            await task.StartAsync(CancellationToken.None);

            Assert.True(Directory.Exists(workDir));
            Assert.True(Directory.Exists(Path.Combine(workDir, "findings")));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
