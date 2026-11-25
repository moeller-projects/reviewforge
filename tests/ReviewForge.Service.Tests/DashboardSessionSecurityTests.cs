using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardSessionSecurityTests
{
    private static ClaimsPrincipal User(string id, string scheme = DashboardSessions.Scheme)
        => new(new ClaimsIdentity([new Claim(DashboardSessions.SessionClaim, id)], scheme));

    [Fact]
    public void Sessions_have_no_key_claims_are_bounded_and_revocable_with_fixed_expiry()
    {
        var clock = new FakeTimeProvider();
        var sessions = new DashboardSessions(clock);
        Assert.False(sessions.IsValid(new ClaimsPrincipal()));
        Assert.False(sessions.IsValid(User("missing")));
        var first = sessions.Create()!.Value;
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromHours(8), first.Expires);
        Assert.True(sessions.IsValid(User(first.Id)));
        Assert.False(sessions.IsValid(User(first.Id, "unrelated")));
        for (var i = 1; i < 10_000; i++) Assert.NotNull(sessions.Create());
        Assert.Null(sessions.Create());
        sessions.Revoke(new ClaimsPrincipal());
        sessions.Revoke(User(first.Id));
        Assert.False(sessions.IsValid(User(first.Id)));
        var last = sessions.Create()!.Value;
        clock.Advance(TimeSpan.FromHours(8));
        Assert.False(sessions.IsValid(User(last.Id)));
        Assert.NotNull(sessions.Create());
    }

    [Fact]
    public async Task Circuit_revalidation_revokes_identity_after_logout_and_expiry()
    {
        var clock = new FakeTimeProvider();
        var sessions = new DashboardSessions(clock);
        using var provider = new DashboardAuthenticationStateProvider(NullLoggerFactory.Instance, sessions);
        Assert.IsAssignableFrom<RevalidatingServerAuthenticationStateProvider>(provider);
        var session = sessions.Create()!.Value;
        var state = new AuthenticationState(User(session.Id));
        provider.SetAuthenticationState(Task.FromResult(state));
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        // Exercise the production revalidator's protected framework callback without waiting 30 wall-clock seconds.
        var method = typeof(DashboardAuthenticationStateProvider).GetMethod("ValidateAuthenticationStateAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        async Task<bool> Validate() => await (Task<bool>)method.Invoke(provider, [state, TestContext.Current.CancellationToken])!;
        Assert.True(await Validate());
        sessions.Revoke(state.User);
        Assert.False(await Validate());
        session = sessions.Create()!.Value;
        state = new AuthenticationState(User(session.Id));
        clock.Advance(TimeSpan.FromHours(8));
        Assert.False(await Validate());
    }
}
