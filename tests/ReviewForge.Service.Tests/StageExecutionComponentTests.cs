using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class StageExecutionComponentTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static RunStatus Status(RunState state = RunState.Failed) => new(Guid.NewGuid(), new("o", "p", "r", 1), state, "private-error", At)
        { StartedAt = At, EnqueuedAt = At.AddSeconds(-2), CurrentStage = "ExecuteReasoning" };
    private static RunDiagnostics Diagnostics(RunStatus s) => new(s.RunId, "trace-correlation", ["FetchPrContext", "ExecuteReasoning", "PersistRun"],
        [new("FetchPrContext", At.AddSeconds(1), At.AddSeconds(2), 1000, "completed", null),
         new("ExecuteReasoning", At.AddSeconds(4), At.AddSeconds(6), 2000, "failed", "stage-failed"),
         new("PersistRun", At.AddSeconds(7), null, null, "failed", "journal-write-failed")],
        new(null, null, "unavailable", null, null, null, null, null, null, [], []));
    [Theory]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("skipped", "Skipped")]
    [InlineData("running", "Incomplete · partial")]
    public async Task Terminal_partial_outcomes_are_textual_and_never_inferred_from_the_plan(string outcome, string label)
    {
        await using var c = Context();
        var s = Status();
        var diagnostic = Diagnostics(s) with { Stages = [new("ExecuteReasoning", At.AddSeconds(-1), null, null, outcome, "private-error")] };
        var page = c.Render<RunExecutionView>(p => p.Add(x => x.Status, s).Add(x => x.Diagnostics, diagnostic));
        Assert.Contains(label, page.Find("[aria-label='Stage inspector']").TextContent);
        Assert.Contains("unavailable", page.Find("[aria-label='Stage inspector']").TextContent);
        Assert.Empty(page.FindAll(".execution-bar"));
        Assert.Single(page.FindAll(".stage-diagnostic"));
        Assert.DoesNotContain("private-error", page.Markup);
        var timelineText = page.Find(".stage-diagnostic").TextContent;
        page.Find("button[aria-label='Show duration table']").Click();
        Assert.Contains(label, page.Find("tbody").TextContent);
        Assert.Contains("unavailable", page.Find("tbody").TextContent);
        Assert.Contains(label, timelineText);
    }

    private static BunitContext Context()
    {
        var c = new BunitContext();
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        return c;
    }

    [Fact]
    public async Task Timeline_and_table_share_measurements_and_sort_without_losing_inspector()
    {
        await using var c = Context();
        var s = Status();
        var page = c.Render<RunExecutionView>(p => p.Add(x => x.Status, s).Add(x => x.Diagnostics, Diagnostics(s)));
        Assert.Contains("Queue wait: 2000 ms", page.Markup);
        Assert.Contains("Current live stage: Analyze changes", page.Markup);
        Assert.Equal(3, page.FindAll(".stage-diagnostic").Count);
        Assert.Equal(2, page.FindAll(".execution-bar").Count);
        Assert.Equal("true", page.Find("button[aria-label='Show stage timeline']").GetAttribute("aria-pressed"));
        Assert.Equal("false", page.Find("[data-stage-index='1']").GetAttribute("aria-pressed"));
        page.Find("[data-stage-index='1']").Click();
        Assert.Contains("stage-failed", page.Find("[aria-label='Stage inspector']").TextContent);
        page.Render(p => p.Add(x => x.Status, s).Add(x => x.Diagnostics, Diagnostics(s)));
        Assert.Equal("true", page.Find("[data-stage-index='1']").GetAttribute("aria-pressed"));
        page.Find("button[aria-label='Show duration table']").Click();
        Assert.Equal(3, page.FindAll("tbody tr").Count);
        Assert.Equal("true", page.Find("button[aria-label='Show duration table']").GetAttribute("aria-pressed"));
        Assert.Equal("true", page.Find("[data-stage-index='1']").GetAttribute("aria-pressed"));
        page.Find("select[aria-label='Stage order']").Change("duration-desc");
        Assert.Equal("1", page.Find("tbody button[data-stage-index]").GetAttribute("data-stage-index"));
        Assert.Contains("Analyze changes", page.Find("[aria-label='Stage inspector']").TextContent);
        page.Find("button[aria-label='Next failed stage']").Click();
        Assert.Contains("journal-write-failed", page.Find("[aria-label='Stage inspector']").TextContent);
        page.Find("button[aria-label='Previous failed stage']").Click();
        Assert.Contains("stage-failed", page.Find("[aria-label='Stage inspector']").TextContent);
        page.Find("button[aria-label='Show stage timeline']").Click();
        Assert.Equal("0", page.Find("button[data-stage-index='1']").GetAttribute("tabindex"));
        Assert.Contains("not execution evidence", page.Markup);
        Assert.DoesNotContain("private-error", page.Markup);
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "attach");
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "focusSelected");
        await page.InvokeAsync(() => page.Instance.DisposeAsync().AsTask());
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "dispose");
    }

    [Fact]
    public async Task Empty_legacy_and_run_change_do_not_invent_execution_or_failures()
    {
        await using var c = Context();
        var s = Status(RunState.Skipped) with { StartedAt = null, EnqueuedAt = null, LoadedStages = ["ReviewGate"] };
        var page = c.Render<RunExecutionView>(p => p.Add(x => x.Status, s));
        Assert.Contains("Durable execution history unavailable", page.Markup);
        Assert.Contains("Queue wait: unavailable", page.Markup);
        Assert.Empty(page.FindAll(".stage-diagnostic"));
        Assert.Empty(page.FindAll("[data-copy-value='trace-correlation']"));
        page.Render(p => p.Add(x => x.Status, s).Add(x => x.Diagnostics, Diagnostics(s) with { Stages = [] }));
        Assert.Contains("No recorded stage executions", page.Markup);
        Assert.True(page.Find("button[aria-label='Next failed stage']").HasAttribute("disabled"));
        var next = Status(RunState.Running);
        page.Render(p => p.Add(x => x.Status, next).Add(x => x.Diagnostics, Diagnostics(next) with {
            Stages = [new("ExecuteReasoning", At, null, null, "running", null)] }));
        Assert.Contains("Active · partial", page.Find("[aria-label='Stage inspector']").TextContent);
        Assert.Empty(page.FindAll(".execution-bar"));
        Assert.Contains(next.RunId.ToString(), page.Find("button[data-copy-kind='Run ID']").GetAttribute("data-copy-value"));
        Assert.Contains("trace-correlation", page.Find("button[data-copy-kind='Trace ID']").GetAttribute("data-copy-value"));
        Assert.NotNull(page.Find("[data-copy-feedback][role='status']"));
        page.Find("[data-stage-index='0']").Click();
        Assert.Contains("unavailable", page.Find("[aria-label='Stage inspector']").TextContent);
    }
}
