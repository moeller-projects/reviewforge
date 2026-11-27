using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace ReviewForge.Service;

/// <summary>Revokes long-lived UI identity after logout or fixed session expiry. HTTP APIs also validate each request.</summary>
public sealed class DashboardAuthenticationStateProvider(ILoggerFactory loggerFactory, DashboardSessions sessions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState,
        CancellationToken cancellationToken) => Task.FromResult(sessions.IsValid(authenticationState.User));
}
