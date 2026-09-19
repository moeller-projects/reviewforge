using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Queue;
using Xunit;

namespace ReviewForge.Service.Tests;

public class InFlightClaimsTests
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);

    [Fact]
    public void Second_claim_is_rejected_with_holder_run_id()
    {
        var claims = new InFlightClaims();
        var first = Guid.NewGuid();

        Assert.True(claims.TryClaim(Key, first, out _));
        Assert.False(claims.TryClaim(Key, Guid.NewGuid(), out var holder));
        Assert.Equal(first, holder);
    }

    [Fact]
    public void Release_allows_reclaim()
    {
        var claims = new InFlightClaims();
        var first = Guid.NewGuid();
        claims.TryClaim(Key, first, out _);

        claims.Release(Key, first);

        Assert.True(claims.TryClaim(Key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Release_by_other_run_does_not_clear_claim()
    {
        var claims = new InFlightClaims();
        claims.TryClaim(Key, Guid.NewGuid(), out _);

        claims.Release(Key, Guid.NewGuid());

        Assert.False(claims.TryClaim(Key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Expired_claim_can_be_replaced()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(2));
        claims.TryClaim(Key, Guid.NewGuid(), out _);

        clock.Advance(TimeSpan.FromHours(3));

        Assert.True(claims.TryClaim(Key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Held_claim_is_only_valid_for_owner_and_ttl()
    {
        var clock = new FakeTimeProvider();
        var runId = Guid.NewGuid();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(1));
        claims.TryClaim(Key, runId, out _);

        Assert.True(claims.IsHeldBy(Key, runId));
        Assert.False(claims.IsHeldBy(Key, Guid.NewGuid()));
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)));
        Assert.False(claims.IsHeldBy(Key, runId));
    }
}
