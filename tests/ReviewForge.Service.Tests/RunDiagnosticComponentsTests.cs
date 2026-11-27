using Bunit;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunDiagnosticComponentsTests
{
    [Fact]
    public async Task Execution_shows_actual_outcomes_durations_failure_codes_and_trace_separately_from_plan()
    {
        await using var c = new BunitContext();
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        var at = DateTimeOffset.UtcNow;
        var status = new RunStatus(Guid.NewGuid(), new("o", "p", "r", 1), RunState.Failed, null, at);
        var diagnostics = new RunDiagnostics(status.RunId, "trace-correlation", ["FetchPrContext", "ExecuteReasoning"],
            [new("FetchPrContext", at, at.AddMilliseconds(12.5), 12.5, "completed", null),
             new("ExecuteReasoning", at, at.AddSeconds(1), 1000, "failed", "stage-failed")], Context());
        var component = c.Render<RunExecutionView>(p => p.Add(x => x.Status, status).Add(x => x.Diagnostics, diagnostics));
        Assert.Equal(2, component.FindAll(".stage-diagnostic").Count);
        Assert.Contains("12.5 ms", component.Markup);
        component.Find("[data-stage-index='1']").Click();
        Assert.Contains("stage-failed", component.Markup);
        Assert.Contains("trace-correlation", component.Markup);
        Assert.Contains("Planned stages", component.Markup);
        Assert.DoesNotContain("until T11", component.Markup);
    }

    [Fact]
    public async Task Legacy_execution_and_context_remain_explicitly_unavailable()
    {
        await using var c = new BunitContext();
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        var status = new RunStatus(Guid.NewGuid(), new("o", "p", "r", 1), RunState.Completed, null, DateTimeOffset.UtcNow)
            { LoadedStages = ["FetchPrContext"] };
        var execution = c.Render<RunExecutionView>(p => p.Add(x => x.Status, status));
        Assert.Contains("Durable execution history unavailable", execution.Markup);
        Assert.Empty(execution.FindAll(".stage-diagnostic"));
        var context = c.Render<RunContextView>();
        Assert.Contains("Run context provenance unavailable", context.Markup);
    }

    [Fact]
    public async Task Context_shows_captured_provenance_summaries_and_evidence_without_raw_markup_or_synthetic_model()
    {
        await using var c = new BunitContext();
        var component = c.Render<RunContextView>(p => p.Add(x => x.Context, Context()));
        Assert.Contains("rules-v1", component.Markup);
        Assert.Contains("unavailable", component.Markup);
        Assert.Contains("&lt;script&gt;", component.Markup);
        Assert.Empty(component.FindAll("script"));
        Assert.Contains("Context missing", component.Markup);
        Assert.Contains("Criterion", component.Markup);
        Assert.Contains("truncated", component.Markup);
    }

    private static RunContextDiagnostic Context() => new("rules-v1", "agentic", "unavailable", "head", "base", 2,
        "<script>summary</script>", "Verifier summary", "PR summary", [new("Topic", "Context missing", "Evidence")],
        [new(42, "Criterion", AcStatus.Unclear, "Evidence")], 1, true);
}
