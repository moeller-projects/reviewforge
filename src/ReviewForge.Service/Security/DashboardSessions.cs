using System.Security.Claims;

namespace ReviewForge.Service;

/// <summary>Process-local, revocable, bounded sessions. Restart intentionally signs everyone out.</summary>
public sealed class DashboardSessions(TimeProvider clock)
{
    public const string Scheme = "Dashboard";
    public const string SessionClaim = "dashboard_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly Dictionary<string, DateTimeOffset> _Sessions = [];
    private readonly object _Lock = new();

    public (string Id, DateTimeOffset Expires)? Create()
    {
        lock (_Lock)
        {
            foreach (var id in _Sessions.Where(x => x.Value <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
                _Sessions.Remove(id);
            if (_Sessions.Count >= 10_000) return null;
            var idNew = Guid.NewGuid().ToString("N");
            var expires = clock.GetUtcNow() + Lifetime;
            _Sessions.Add(idNew, expires);
            return (idNew, expires);
        }
    }

    public bool IsValid(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(SessionClaim);
        lock (_Lock)
            return user.Identity?.AuthenticationType == Scheme && id is not null
                && _Sessions.TryGetValue(id, out var expiry) && expiry > clock.GetUtcNow();
    }

    public void Revoke(ClaimsPrincipal user)
    {
        lock (_Lock)
            if (user.FindFirstValue(SessionClaim) is { } id) _Sessions.Remove(id);
    }
}

/// <summary>Opt-in endpoint metadata; never grants session access to other API routes.</summary>
public sealed class DashboardSessionAccess;
