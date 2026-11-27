using Bunit;
using Microsoft.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Profiling;
using ReviewForge.Service.Components.Dashboard;
using System.Text.Json;
using Xunit;

namespace ReviewForge.Service.Tests;

public class ProfileExplorerTests
{
    private static ModelProfilePage Fixture(int count = 4)
    {
        var run = Guid.NewGuid(); var profile = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var logicalId = Guid.NewGuid(); var attemptId = Guid.NewGuid(); var providerId = Guid.NewGuid();
        var provider = new ModelCallRecord(run, profile, providerId, 3, "ExecuteReasoning", ModelCallProfile.Review,
            "openai", "gpt-5", start.AddMilliseconds(20), start.AddMilliseconds(100), 80, "completed", 100, null, 120,
            Cost: 0.5m, Currency: "USD", CostProvenance: "provider-reported", ParentCallId: attemptId,
            AttemptNumber: 2, LogicalCallId: logicalId, AttemptId: attemptId);
        ExecutionActivityRecord Activity(Guid id, long sequence, string kind, Guid? parent) => new(run, profile, id, sequence,
            kind, "ExecuteReasoning", ModelCallProfile.Review, parent, logicalId, attemptId, 2, kind == "tool" ? "ReadFile" : null,
            start.AddMilliseconds(sequence), start.AddMilliseconds(100), 100 - sequence, "completed");
        return new("available", run, profile, count, false, false,
            Enumerable.Range(0, Math.Max(1, count - 3)).Select(i => provider with { CallId = i == 0 ? providerId : Guid.NewGuid(), Sequence = i + 3 }).ToArray(), 50,
            Activities: [Activity(logicalId, 1, "logical", null), Activity(attemptId, 2, "attempt", logicalId), Activity(Guid.NewGuid(), count, "tool", providerId)]);
    }

    [Fact]
    public void Projection_keeps_overlap_separate_sorts_nulls_last_and_filters_only_safe_metadata()
    {
        var page = Fixture();
        var rows = ProfileExplorerPresentation.Rows(page);
        Assert.Equal(new[] { "logical", "attempt", "provider", "tool" }, rows.Select(r => r.Kind));
        var provider = rows[2];
        Assert.Equal(rows[1].Id, provider.Parent);
        Assert.Equal("0.5 USD (provider-reported)", ProfileExplorerPresentation.Cost(provider));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Cost(provider with { CostProvenance = "estimated" }));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Cost(provider with { Currency = null }));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Cost(provider with { Cost = -1 }));
        Assert.Contains("width:", ProfileExplorerPresentation.Bar(provider, rows));
        Assert.Equal("", ProfileExplorerPresentation.Bar(provider with { End = null }, rows));
        Assert.Equal("", ProfileExplorerPresentation.Bar(provider with { End = provider.Start.AddDays(-1) }, rows));
        Assert.Null(ProfileExplorerPresentation.Valid(double.NaN));
        Assert.Null(ProfileExplorerPresentation.Valid(double.PositiveInfinity));
        Assert.Null(ProfileExplorerPresentation.Valid(-1));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Duration(-1));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Identifier(null));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Time(null));
        Assert.Equal("unavailable", ProfileExplorerPresentation.Number(null));
        var unavailable = provider with { Id = Guid.NewGuid(), Duration = null };
        var source = rows.Append(unavailable).ToArray();
        IReadOnlyList<ProfileExplorerRow> Filter(string search = "", string kind = "", string outcome = "", string model = "", string tool = "", string stage = "", string duration = "", string order = "") =>
            ProfileExplorerPresentation.Filter(source, search, kind, outcome, model, tool, stage, duration, order);
        Assert.Equal(unavailable.Id, Filter(order: "duration-desc")[^1].Id);
        Assert.Equal(unavailable.Id, Filter(order: "duration-asc")[^1].Id);
        Assert.Single(Filter(duration: "missing"));
        Assert.Empty(Filter(duration: "slow"));
        Assert.Single(Filter(tool: "ReadFile"));
        Assert.Equal(2, Filter(model: "gpt-5").Count);
        Assert.Empty(Filter(stage: "ApplyFixes"));
        Assert.Empty(Filter(outcome: "failed"));
        Assert.Single(Filter(kind: "attempt"));
        Assert.NotEmpty(Filter(search: "analyze CHANGES"));
        Assert.Equal(2, Filter(search: provider.Id.ToString()).Count); // Provider identity and its tool's parent; clone has a new ID.
    }

    [Fact]
    public async Task Repeating_stage_selection_reapplies_the_filter_after_clearing_it()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<ProfileExplorer>(p => p.Add(x => x.Page, Fixture()).Add(x => x.StageFilter, "ExecuteReasoning"));
        await component.InvokeAsync(() => component.Instance.ExecuteProfileCommand("clear"));
        Assert.Equal("", component.Find("[aria-label='Profile stage']").GetAttribute("value"));
        component.Render(p => p.Add(x => x.StageFilter, "ExecuteReasoning").Add(x => x.StageFilterVersion, 1));
        Assert.Equal("ExecuteReasoning", component.Find("[aria-label='Profile stage']").GetAttribute("value"));
    }

    [Fact]
    public async Task Disposal_during_controller_attachment_retires_late_controller_and_ignores_callbacks()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = new LateController();
        context.Services.AddSingleton<IJSRuntime>(module);
        var component = context.Render<ProfileExplorer>(p => p.Add(x => x.Page, Fixture()));
        var instance = component.Instance;
        await context.DisposeComponentsAsync();
        var controller = new LateController();
        module.Attach.SetResult(controller);
        await controller.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.DoesNotContain("syncPage", controller.Calls);
        Assert.Contains("dispose", controller.Calls);
        await instance.ExecuteProfileCommand("commands");
    }

    // Single-use delayed-controller double at the interop lifecycle boundary.
#pragma warning disable BL0016 // Controlled single-use interop double; no real JS/circuit calls occur.
    private sealed class LateController : IJSRuntime, IJSObjectReference
    {
        public List<string> Calls { get; } = [];
        public TaskCompletionSource<IJSObjectReference> Attach { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            if (identifier == "import") return (TValue)(object)this;
            if (identifier == "attach") return (TValue)(object)await Attach.Task;
            return default!;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
#pragma warning restore BL0016

    [Fact]
    public async Task Leaving_execution_and_returning_preserves_the_loaded_investigation_without_another_read()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        context.Services.AddSingleton(TimeProvider.System);
        var fixture = Fixture();
        var details = new RunDetailsResponse(new(fixture.RunId, new("o", "p", "r", 1), RunState.Completed,
            null, DateTimeOffset.UtcNow), "head", true, null, []);
        var http = context.JSInterop.SetupModule("./dashboard-http.js");
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{fixture.RunId:D}/details"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(details)));
        var calls = http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => i.Arguments[2]?.ToString()?.Contains("model-calls") == true);
        calls.SetResult(new(200, JsonSerializer.SerializeToElement(fixture)));
        var component = context.Render<ReviewForge.Service.Components.Pages.RunDetail>(p => p.Add(x => x.RunId, fixture.RunId));
        void Section(string name) => component.FindAll(".run-sections button").Single(b => b.TextContent == name).Click();
        Section("Execution");
        component.FindAll("button").Single(b => b.TextContent == "Load model calls").Click();
        var explorer = component.FindComponent<ProfileExplorer>().Instance;
        component.Find("[aria-label='Profile search']").Input("ReadFile");
        component.Find("[aria-label='Profile view']").Change("timeline");
        Section("Context"); Section("Execution");
        Assert.Same(explorer, component.FindComponent<ProfileExplorer>().Instance);
        Assert.Single(component.FindAll("[data-profile-row]"));
        Assert.Contains("Chronological metadata timeline", component.Markup);
        Assert.Single(http.Invocations, i => i.Identifier == "send" && i.Arguments[2]?.ToString()?.Contains("model-calls") == true);
    }

    [Fact]
    public async Task Ten_thousand_detail_fixture_renders_one_bounded_page_and_never_accumulates_previous_pages()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var fixture = Fixture(10_000);
        ModelProfilePage Page(long cursor) => fixture with {
            Calls = fixture.Calls.Where(r => r.Sequence > cursor && r.Sequence <= cursor + 50).ToArray(),
            Activities = fixture.Activities!.Where(r => r.Sequence > cursor && r.Sequence <= cursor + 50).ToArray(),
            NextSequence = cursor + 50 < 10_000 ? cursor + 50 : null
        };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var component = context.Render<ProfileExplorer>(p => p.Add(x => x.Page, Page(0)));
        Assert.Equal(50, component.FindAll("[data-profile-row]").Count);
        int maxMarkup = 0, maxPayload = 0;
        for (var cursor = 0; cursor < 10_000; cursor += 50)
        {
            var page = Page(cursor);
            component.Render(p => p.Add(x => x.Page, page).Add(x => x.PageCursor, cursor));
            Assert.Equal(50, component.FindAll("[data-profile-row]").Count);
            maxMarkup = Math.Max(maxMarkup, component.Markup.Length);
            maxPayload = Math.Max(maxPayload, JsonSerializer.SerializeToUtf8Bytes(page).Length);
            Assert.True(maxMarkup < 120_000);
            Assert.True(maxPayload < 100_000);
            Assert.Single(component.FindAll("[aria-label='Profile record inspector']"));
        }
        Assert.Equal(50, ProfileExplorerPresentation.Rows(fixture).Count);
        Xunit.TestContext.Current.TestOutputHelper!.WriteLine($"T12 synthetic fixture: 10,000 mixed details, 200 explicit pages, 50 rows/page, one pinned inspector; max markup {maxMarkup} chars, max serialized page payload {maxPayload} bytes; {timer.ElapsedMilliseconds} ms traversal (bUnit, not browser/network latency).");
    }

    [Fact]
    public async Task Correlation_navigation_clears_page_filters_and_commands_share_the_same_read_only_actions()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var page = Fixture(); string? stage = null;
        var component = context.Render<ProfileExplorer>(p => p.Add(x => x.Page, page).Add(x => x.StageSelected, value => stage = value));
        component.FindAll("[data-profile-row]").Single(b => b.TextContent.StartsWith("provider", StringComparison.Ordinal)).Click();
        Assert.Contains("0.5 USD", component.Markup);
        var inspector = component.Find("[aria-label='Profile record inspector']");
        inspector.QuerySelectorAll("button").Single(b => b.TextContent.Contains("Open Parent on page")).Click();
        Assert.Contains("attempt", component.Find("h4").TextContent);
        inspector = component.Find("[aria-label='Profile record inspector']");
        inspector.QuerySelectorAll("button").Single(b => b.TextContent.Contains("Analyze changes")).Click();
        Assert.Equal("ExecuteReasoning", stage);
        component.Find("[aria-label='Profile tool']").Change("ReadFile"); Assert.Single(component.FindAll("[data-profile-row]"));
        component.Find("[aria-label='Profile model']").Change("gpt-5"); Assert.Empty(component.FindAll("[data-profile-row]"));
        component.Find("[aria-label='Profile duration']").Change("missing");
        component.Find("[aria-label='Profile stage']").Change("ApplyFixes");
        await component.InvokeAsync(() => component.Instance.ExecuteProfileCommand("clear"));
        Assert.Equal(4, component.FindAll("[data-profile-row]").Count);
        component.Find("[aria-label='Profile order']").Change("duration-asc");
        component.Find("[aria-label='Profile view']").Change("timeline");
        Assert.Contains("Chronological metadata timeline", component.Markup);
        await component.InvokeAsync(() => component.Instance.ExecuteProfileCommand("commands"));
        Assert.Single(component.FindAll("dialog"));
        component.FindAll("dialog button").Single(b => b.TextContent == "Show semantic table").Click();
        Assert.Empty(component.FindAll("dialog")); Assert.Single(component.FindAll("table"));
        foreach (var command in new[] { "search", "timeline", "return", "unknown" })
            await component.InvokeAsync(() => component.Instance.ExecuteProfileCommand(command));
        component.Render(p => p.Add(x => x.Page, page).Add(x => x.StageFilter, "ApplyFixes"));
        Assert.Empty(component.FindAll("[data-profile-row]"));
        component.Render(p => p.Add(x => x.Page, page with { State = "expired", Calls = [], Activities = [] }));
        Assert.Empty(component.FindAll("[aria-label='Profile record inspector']"));
    }

    [Fact]
    public async Task Retained_usage_is_page_independent_and_reported_costs_remain_page_scoped_by_currency()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        var collector = new ModelProfileCollector(Guid.NewGuid());
        collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5")
            .Complete("completed", new() { InputTokenCount = 12, OutputTokenCount = 0 });
        collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
        var aggregate = collector.Snapshot().Aggregate;
        var fixture = Fixture();
        var page = fixture with { Aggregate = aggregate, ObservedCalls = 2 };
        var summaries = context.Render<ProfileSummaries>(p => p.Add(x => x.Page, page));
        var retained = summaries.Markup;
        var retainedText = summaries.FindAll("p, caption, th, td").Select(e => e.TextContent).ToArray();
        Assert.Contains("12 (partial; 1/2 calls reported)", retained);
        Assert.Contains("0 (partial; 1/2 calls reported)", retained);
        Assert.Contains("Whole-run provider-reported cost: unavailable", retained);
        summaries.Render(p => p.Add(x => x.Page, page with { Calls = [], Activities = [], State = "expired" }));
        Assert.Equal(retainedText, summaries.FindAll("p, caption, th, td").Select(e => e.TextContent));
        var provider = page.Calls[0];
        var explorer = context.Render<ProfileExplorer>(p => p.Add(x => x.Page, page with { Calls = [
            provider,
            provider with { CallId = Guid.NewGuid(), Sequence = 10, Cost = 2, Currency = "EUR" },
            provider with { CallId = Guid.NewGuid(), Sequence = 11, Cost = 999, CostProvenance = "estimated" },
            provider with { CallId = Guid.NewGuid(), Sequence = 12, Cost = null }
        ] }));
        var costs = explorer.FindAll("p").Single(p => p.TextContent.Contains("Loaded-page provider-reported cost only")).TextContent;
        Assert.Contains("0.5 USD", costs); Assert.Contains("2 EUR", costs);
        Assert.DoesNotContain("999", costs); Assert.DoesNotContain("2.5", costs);
        Assert.Contains("partial, not whole-run billing", costs);
    }

    [Fact]
    public async Task Paging_retains_inspector_and_read_error_does_not_destroy_last_read()
    {
        await using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        var page = Fixture();
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        var send = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        void Return(ModelProfilePage value) => send.SetResult(new(200, JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Return(page);
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status, new RunStatus(page.RunId, new("o", "p", "r", 1), RunState.Completed, null, DateTimeOffset.UtcNow)));
        component.Find("button").Click();
        var first = component.FindAll("[data-profile-row]")[0].TextContent;
        Return(page with { Calls = [page.Calls[0] with { CallId = Guid.NewGuid(), Sequence = 51 }], Activities = [], NextSequence = 100 });
        component.FindAll("button").Single(b => b.TextContent == "Next model calls").Click();
        Assert.Contains("Page starts after sequence 50", component.Markup);
        Assert.Contains("outside the current page", component.Markup);
        Return(page);
        component.FindAll("button").Single(b => b.TextContent == "Previous profile page").Click();
        Assert.Contains(first, component.FindAll("[data-profile-row]")[0].TextContent);
        send.SetResult(new(503, null));
        component.Find("button").Click();
        Assert.Contains("last successful read", component.Markup);
        Assert.Equal(4, component.FindAll("[data-profile-row]").Count);
        Assert.Equal(4, module.Invocations.Count(i => i.Identifier == "send"));
    }

    [Fact]
    public async Task Explorer_filters_only_current_page_and_keeps_selected_inspector_and_retained_totals()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddScoped<DashboardBrowserClient>();
        var id = Guid.NewGuid();
        var collector = new ModelProfileCollector(id);
        collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
        using (ModelCallContext.Enter(collector, "ExecuteReasoning", ModelCallProfile.Review))
            collector.BeginActivity("tool", "ReadFile").Complete("failed");
        var snapshot = collector.Snapshot();
        var page = new ModelProfilePage("truncated", id, snapshot.ProfileId, 999, true, true, snapshot.Calls, 2,
            Aggregate: snapshot.Aggregate, Activities: snapshot.Activities);
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(200,
            JsonSerializer.SerializeToElement(page, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var component = context.Render<ModelCallTable>(p => p.Add(x => x.Status,
            new RunStatus(id, new("o", "p", "r", 1), RunState.Failed, null, DateTimeOffset.UtcNow)));
        component.Find("button").Click();
        Assert.Contains("Whole-run provider calls: 999", component.Markup);
        Assert.Contains("Filters and search cover this loaded page only", component.Markup);
        Assert.Equal(2, component.FindAll("[data-profile-row]").Count);
        component.FindAll("[data-profile-row]")[1].Click();
        component.Find("[aria-label='Profile search']").Input("no-match");
        Assert.Empty(component.FindAll("[data-profile-row]"));
        Assert.Contains("Selected record is outside the current page or filters", component.Markup);
        Assert.Contains("ReadFile", component.Find("[aria-label='Profile record inspector']").TextContent);
        Assert.Contains("Whole-run provider calls: 999", component.Markup);
        component.Find("[aria-label='Profile search']").Input("");
        component.Find("[aria-label='Profile type']").Change("provider");
        Assert.Single(component.FindAll("[data-profile-row]"));
        component.Find("[aria-label='Profile type']").Change("");
        component.Find("[aria-label='Profile outcome']").Change("failed");
        Assert.Single(component.FindAll("[data-profile-row]"));
        Assert.Contains("missing-parent", component.Markup);
        component.Find("[aria-label='Profile outcome']").Change("");
        component.Find("[aria-label='Profile view']").Change("timeline");
        Assert.Equal(2, component.FindAll("[data-profile-row]").Count);
        Assert.Contains("Chronological metadata timeline", component.Markup);
    }
}
