using System.Diagnostics.Metrics;
using ReviewForge.Core.Pipeline;

namespace ReviewForge.Core.Pipeline;

public static class LlmTelemetry
{
    public static readonly Counter<long> LlmTokens =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.llm.tokens_total", "{token}"); // tags: token_type, model

    public static readonly Counter<long> LlmCachedTokens =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.llm.tokens.cached_total", "{token}"); // tags: model

    public static readonly Counter<long> LlmRequests =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.llm.requests_total"); // tags: model

    public static readonly Histogram<double> LlmGovernorWait =
        ReviewForgeTelemetry.Meter.CreateHistogram<double>("reviewforge.llm.governor.wait_ms", "ms"); // slot acquisition wait

    public static readonly Counter<long> LlmGovernorTimeouts =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.llm.governor.timeout_total"); // slot acquisition timed out

    public static void RegisterGauges(Func<int> llmInflight)
    {
        ReviewForgeTelemetry.Meter.CreateObservableGauge("reviewforge.llm.governor.inflight", llmInflight);
    }
}
