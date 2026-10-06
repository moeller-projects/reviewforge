using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class ClaimsTelemetry
{
    public static readonly Counter<long> ClaimAcquired =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.claims.acquired_total");

    public static readonly Counter<long> ClaimRejected =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.claims.rejected_total"); // TryClaim returned false

    public static readonly Counter<long> ClaimRenewed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.claims.renewed_total");

    public static readonly Counter<long> ClaimRenewalFailed =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.claims.renewal_failed_total"); // Renew returned false

    public static readonly Counter<long> ClaimExpired =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.claims.expired_total"); // expired entry observed

    public static void RegisterGauges(Func<int> activeClaims)
    {
        ReviewForgeTelemetry.Meter.CreateObservableGauge("reviewforge.claims.active", activeClaims);
    }
}
