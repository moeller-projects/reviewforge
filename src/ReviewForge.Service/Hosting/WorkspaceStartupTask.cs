using Microsoft.Extensions.Options;

namespace ReviewForge.Service;

/// <summary>Creates the workspace directories before any hosted worker can dequeue work.</summary>
public sealed class WorkspaceStartupTask(IOptions<WorkspaceOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var workDir = options.Value.WorkDir;
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.Combine(workDir, "findings"));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
