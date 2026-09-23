using ReviewForge.Core.Domain;

namespace ReviewForge.Service.Queue;

/// <summary>
/// Periodically renews an in-flight claim while its run executes, so a long review does not
/// let the reservation expire and admit a duplicate. Stops silently when the claim is lost —
/// the run's publish guard then fails the run safely instead of posting twice.
/// </summary>
public sealed class ClaimHeartbeat(
    InFlightClaims claims,
    PrKey pr,
    Guid runId,
    TimeSpan interval,
    TimeProvider? time = null)
{
    public async Task RunUntilCancelled(CancellationToken ct)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval, time ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!claims.Renew(pr, runId))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}