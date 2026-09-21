using ReviewForge.Core.Workspaces;
using ReviewForge.Service.Queue;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class TelemetryGaugeRegistrationTests : IDisposable
{
    private readonly string _Root = Path.Combine(Path.GetTempPath(), "reviewforge-gauges-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_Root))
        {
            Directory.Delete(_Root, recursive: true);
        }
    }

    [Fact]
    public async Task Start_registers_gauges_and_stop_completes()
    {
        var pool = new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), _Root);
        var service = new TelemetryGaugeRegistration(new ReviewQueue(), new InFlightClaims(), pool);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
