using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Queue;

/// <summary>
/// In-flight reservation per PR. The review gate's PriorRun read is not a reservation, so
/// without a claim two runs of the same PR can both decide Full and double-publish. Claims
/// carry a TTL so a missed release cannot wedge a PR forever.
/// </summary>
public sealed class InFlightClaims(TimeProvider? clock = null, TimeSpan? ttl = null)
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(2);
    private readonly Dictionary<PrKey, ClaimEntry> _Claims = [];

    private readonly TimeProvider _Clock = clock ?? TimeProvider.System;
    private readonly object _Gate = new();
    private readonly TimeSpan _Ttl = ttl ?? DefaultTtl;

    /// <summary>The claim lifetime used to expire queued/crashed reservations.</summary>
    public TimeSpan Ttl => _Ttl;

    /// <summary>Reserves the PR for this run; false (with the holder's run id) when already claimed.</summary>
    public bool TryClaim(PrKey pr, Guid runId, out Guid? holder)
    {
        lock (_Gate)
        {
            var now = _Clock.GetUtcNow();
            if (_Claims.TryGetValue(pr, out var existing) && now - existing.ClaimedAt <= _Ttl)
            {
                holder = existing.RunId;
                return false;
            }

            _Claims[pr] = new ClaimEntry(runId, now);
            holder = null;
            return true;
        }
    }

    /// <summary>Releases the reservation, but only when this run still holds it.</summary>
    public void Release(PrKey pr, Guid runId)
    {
        lock (_Gate)
        {
            if (_Claims.TryGetValue(pr, out var existing) && existing.RunId == runId)
            {
                _Claims.Remove(pr);
            }
        }
    }

    public bool IsHeldBy(PrKey pr, Guid runId)
    {
        lock (_Gate)
        {
            return _Claims.TryGetValue(pr, out var existing)
                   && existing.RunId == runId
                   && _Clock.GetUtcNow() - existing.ClaimedAt <= _Ttl;
        }
    }

    /// <summary>
    /// Pushes the owner's expiry forward; false when the claim was lost (expired or taken by
    /// another run). Called periodically while a run is actively executing.
    /// </summary>
    public bool Renew(PrKey pr, Guid runId)
    {
        lock (_Gate)
        {
            if (_Claims.TryGetValue(pr, out var existing) && existing.RunId == runId)
            {
                _Claims[pr] = existing with {ClaimedAt = _Clock.GetUtcNow()};
                return true;
            }

            return false;
        }
    }

    private sealed record ClaimEntry(Guid RunId, DateTimeOffset ClaimedAt);
}