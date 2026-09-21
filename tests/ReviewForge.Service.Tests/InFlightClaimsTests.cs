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

    [Fact]
    public void Renew_pushes_expiry_forward()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(1));
        var runId = Guid.NewGuid();
        claims.TryClaim(Key, runId, out _);

        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.True(claims.Renew(Key, runId));

        // Beyond the original TTL, but within the renewed lease, the claim still blocks competitors.
        clock.Advance(TimeSpan.FromMinutes(50));
        Assert.True(claims.IsHeldBy(Key, runId));
        Assert.False(claims.TryClaim(Key, Guid.NewGuid(), out _));
    }

    [Fact]
    public void Renew_returns_false_when_claim_lost()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromHours(1));
        var first = Guid.NewGuid();
        claims.TryClaim(Key, first, out _);

        // Expire the first claim so a competitor replaces it.
        clock.Advance(TimeSpan.FromHours(2));
        var second = Guid.NewGuid();
        Assert.True(claims.TryClaim(Key, second, out _));

        Assert.False(claims.Renew(Key, first));
        Assert.True(claims.Renew(Key, second));
    }

    [Fact]
    public async Task Simultaneous_claims_admit_exactly_one_winner()
    {
        var claims = new InFlightClaims();
        var ids = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();

        // Contend the claim boundary from many threads: exactly one must reserve the PR.
        var results = await Task.WhenAll(ids.Select(id => Task.Run(() => claims.TryClaim(Key, id, out _))));

        Assert.Equal(1, results.Count(win => win));
    }

    [Fact]
    public async Task ClaimHeartbeat_keeps_the_claim_alive_across_its_ttl()
    {
        var clock = new FakeTimeProvider();
        var claims = new InFlightClaims(clock, TimeSpan.FromMinutes(1));
        var runId = Guid.NewGuid();
        Assert.True(claims.TryClaim(Key, runId, out _));

        // The claim has already aged past its TTL by the time the heartbeat starts.
        clock.Advance(TimeSpan.FromMinutes(2));

        using var cts = new CancellationTokenSource();
        var heartbeat = new ClaimHeartbeat(claims, Key, runId, TimeSpan.FromMilliseconds(20))
            .RunUntilCancelled(cts.Token);

        await Task.Delay(80); // allow at least one real tick to renew against the advanced clock

        Assert.True(claims.IsHeldBy(Key, runId));

        cts.Cancel();
        await heartbeat;
    }

    [Fact]
    public void ActiveCount_reflects_live_claims()
    {
        var claims = new InFlightClaims();
        Assert.Equal(0, claims.ActiveCount);

        var runId = Guid.NewGuid();
        Assert.True(claims.TryClaim(Key, runId, out _));
        Assert.Equal(1, claims.ActiveCount);

        claims.Release(Key, runId);
        Assert.Equal(0, claims.ActiveCount);
    }
}