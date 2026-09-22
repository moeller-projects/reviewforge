using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Queue;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ClaimHeartbeatTests
{
    private static readonly PrKey Key = new("o", "p", "r", 1);

    [Fact]
    public async Task Zero_interval_returns_immediately()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(1));
        var heartbeat = new ClaimHeartbeat(claims, Key, Guid.NewGuid(), TimeSpan.Zero);

        await heartbeat.RunUntilCancelled(CancellationToken.None);
        // Returning at all proves the loop was skipped; a tick loop would hang here.
    }

    [Fact]
    public async Task Stops_when_the_claim_is_lost()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(1));
        var holderRun = Guid.NewGuid();
        Assert.True(claims.TryClaim(Key, holderRun, out _));

        // Heartbeat for a different run: the first renewal fails and it must stop
        // rather than spin until cancellation.
        var heartbeat = new ClaimHeartbeat(claims, Key, Guid.NewGuid(), TimeSpan.FromMilliseconds(5));

        await heartbeat.RunUntilCancelled(CancellationToken.None);

        Assert.True(claims.IsHeldBy(Key, holderRun));
    }
}