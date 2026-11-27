using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ModelCallTableTests
{
    [Fact]
    public async Task Expired_details_still_show_basic_stage_summaries_and_honest_usage_coverage()
    {
        await using var context = new BunitContext();
        context.JSInterop.SetupModule("./dashboard-profiler.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        var id = Guid.NewGuid();
        var collector = new ReviewForge.Core.Profiling.ModelProfileCollector(id, 0);
        collector.Begin("ExecuteReasoning", ReviewForge.Core.Profiling.ModelCallProfile.Review, "openai", "gpt-5")
            .Complete("completed", new() { InputTokenCount = 12, OutputTokenCount = 0 });
        collector.Begin("ExecuteReasoning", ReviewForge.Core.Profiling.ModelCallProfile.Review, "openai", "gpt-5").Complete("incomplete", null);
        var snapshot = collector.Snapshot();
        var page = new ReviewForge.Core.Ports.ModelProfilePage("expired", id, snapshot.ProfileId, 2, true, false, [], null, Aggregate: snapshot.Aggregate);
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, System.Text.Json.JsonSerializer.SerializeToElement(page, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status,
            new RunStatus(id, new("o", "p", "r", 1), RunState.Completed, null, DateTimeOffset.UtcNow)));
        component.Find("button").Click();
        Assert.Contains("Retained stage summaries", component.Markup);
        Assert.Contains("Analyze changes", component.Markup);
        Assert.Contains("Provider-incomplete", component.Markup);
        Assert.Contains("12 (partial; 1/2 calls reported)", component.Markup);
        Assert.Contains("0 (partial; 1/2 calls reported)", component.Markup);
        Assert.Contains("unavailable", component.Markup);
        Assert.DoesNotContain("Capture is incomplete", component.Markup);
        Assert.Single(component.FindAll("tbody tr"));
    }

    [Fact]
    public async Task Correlated_activity_rows_and_retained_counts_are_separate_from_provider_calls()
    {
        await using var context = new BunitContext();
        context.JSInterop.SetupModule("./dashboard-profiler.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        var id = Guid.NewGuid();
        var collector = new ReviewForge.Core.Profiling.ModelProfileCollector(id);
        using var scope = ReviewForge.Core.Profiling.ModelCallContext.Enter(collector, "ApplyFixes", ReviewForge.Core.Profiling.ModelCallProfile.Fix);
        var logical = collector.BeginActivity("logical");
        using var logicalScope = ReviewForge.Core.Profiling.ModelCallContext.EnterCall(logical.Id);
        var attempt = collector.BeginActivity("attempt", attemptNumber: 1);
        attempt.Wait(12);
        attempt.Wait(4, true);
        attempt.Complete("failed");
        collector.BeginActivity("tool", "EditFile").Complete("cancelled");
        logical.Complete("failed");
        var snapshot = collector.Snapshot();
        var page = new ReviewForge.Core.Ports.ModelProfilePage("available", id, snapshot.ProfileId, 0, false, false, [], null,
            Aggregate: snapshot.Aggregate, Activities: snapshot.Activities);
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, System.Text.Json.JsonSerializer.SerializeToElement(page, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status,
            new RunStatus(id, new("o", "p", "r", 1), RunState.Failed, null, DateTimeOffset.UtcNow)));
        component.Find("button").Click();
        Assert.Contains("Application attempts: 1", component.Markup);
        Assert.Contains("Tools: 1", component.Markup);
        Assert.Contains("Logical calls: 1", component.Markup);
        Assert.Contains("Tool and application-retry metadata", component.Markup);
        Assert.Contains("EditFile", component.Markup);
        Assert.Contains(logical.Id.ToString(), component.Markup);
        Assert.Contains("missing-parent", component.Markup);
        component.FindAll("[data-profile-row]").First(r => r.TextContent.Contains("attempt")).Click();
        Assert.Contains("Governor wait / backoff", component.Markup);
        Assert.Contains("12 / 4", component.Markup);
        Assert.Contains("SDK-internal", component.Markup);
    }

    [Theory]
    [InlineData("legacy-unavailable", "legacy run")]
    [InlineData("expired", "expired")]
    [InlineData("empty", "No model calls were observed")]
    [InlineData("incomplete", "Incomplete model-call capture")]
    [InlineData("truncated", "Truncated model-call capture")]
    [InlineData("available", "Recorded model calls")]
    [InlineData("not-yet-available", "not yet available")]
    [InlineData("skipped-not-recorded", "Skipped runs")]
    public async Task Reads_render_distinct_coverage_and_paginate_safe_metadata(string state, string label)
    {
        await using var context = new BunitContext();
        context.JSInterop.SetupModule("./dashboard-profiler.js").Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        var id = Guid.NewGuid();
        var collector = new ReviewForge.Core.Profiling.ModelProfileCollector(id);
        collector.Begin("ExecuteReasoning", ReviewForge.Core.Profiling.ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
        var snapshot = collector.Snapshot();
        var payload = new ReviewForge.Core.Ports.ModelProfilePage(state, id, snapshot.ProfileId, 2, true, true, snapshot.Calls, 1);
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, System.Text.Json.JsonSerializer.SerializeToElement(payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));
        var status = new RunStatus(id, new("o", "p", "r", 1), RunState.Completed, null, DateTimeOffset.UtcNow);
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status, status));
        component.Find("button").Click();
        Assert.Contains(label, component.Markup);
        Assert.Contains("Detail cap reached", component.Markup);
        Assert.Contains("Capture is incomplete", component.Markup);
        Assert.Contains("unavailable / unavailable / unavailable", component.Markup);
        Assert.Single(component.FindAll("tbody tr"));
        component.FindAll("button").Last().Click();
        Assert.Contains(module.Invocations, call => call.Identifier == "send" && call.Arguments[2]!.ToString()!.Contains("after=1&count=50"));
        component.Render(p => p.Add(x => x.Status, status with { RunId = Guid.NewGuid() }));
        Assert.Empty(component.FindAll("tbody tr"));
        Assert.Contains("Load model calls", component.Markup);
    }

    [Theory]
    [InlineData(401, "Session expired")]
    [InlineData(503, "Could not read")]
    public async Task Failed_reads_clear_table_and_offer_explicit_recovery(int statusCode, string message)
    {
        await using var context = new BunitContext();
        context.Services.AddScoped<DashboardBrowserClient>();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(statusCode, null));
        var status = new RunStatus(Guid.NewGuid(), new("o", "p", "r", 1), RunState.Failed, null, DateTimeOffset.UtcNow);
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status, status));
        component.Find("button").Click();
        Assert.Contains(message, component.Markup);
        Assert.Empty(component.FindAll("tbody tr"));
    }

    [Theory]
    [InlineData(RunState.Running, "available after execution ends")]
    [InlineData(RunState.Queued, "available after execution ends")]
    [InlineData(RunState.Skipped, "Skipped runs do not retain")]
    [InlineData(RunState.Failed, "Load model calls")]
    [InlineData(RunState.Completed, "Load model calls")]
    public async Task Active_skipped_and_terminal_states_are_distinct(RunState state, string expected)
    {
        await using var context = new BunitContext();
        context.Services.AddScoped<DashboardBrowserClient>();
        var status = new RunStatus(Guid.NewGuid(), new("o", "p", "r", 1), state, null, DateTimeOffset.UtcNow);
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status, status));
        Assert.Contains(expected, component.Markup);
        Assert.Empty(context.JSInterop.Invocations);
    }
}
