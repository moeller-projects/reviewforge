using System.Diagnostics.Metrics;

namespace ReviewForge.Core.Telemetry;

public static class LlmTelemetry
{
    public static readonly Counter<long> LlmCachedTokens =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.llm.tokens.cached_total", "{token}"); // tags: model


    public static readonly Histogram<int> AgentIterations =
        ReviewForgeTelemetry.Meter.CreateHistogram<int>("reviewforge.agent.iterations", "{iteration}"); // model turns per review

    public static readonly Counter<long> AgentTaskDoneMissing =
        ReviewForgeTelemetry.Meter.CreateCounter<long>("reviewforge.agent.task_done_missing_total"); // hit iteration cap w/o task_done — tags: model

}