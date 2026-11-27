using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class FindingDecisionRegistrationTests
{
    [Fact]
    public void Decision_port_uses_the_swapped_finding_store_instance()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        Assert.Same(factory.Store, factory.Services.GetRequiredService<IFindingDecisionStore>());
        Assert.NotNull(factory.Services.GetRequiredService<FindingDecisionService>());
    }
}
