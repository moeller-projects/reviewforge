using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using Radzen.Blazor;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using ReviewForge.Service.Components.Pages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardComponentTests
{
    private static DashboardRow Row(int id = 1, string repo = "Repository") => new(
        new(new(new("org", "Project", repo, id), new FakePullRequestSource().Pr, "main", "author", "Ada"), repo, "https://example.com/pr"),
        false, true, Eligibility: new(BasicEligibilityStatus.Unknown, "Human comments not checked"));
    private static DashboardResponse Data(params DashboardRow[] rows) => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-30),
        true, [], rows, new(rows.Length, 0, 0, rows.Length, false));
    private static BunitContext Context()
    {
        var c = new BunitContext();
        c.Services.AddRadzenComponents();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        return c;
    }
    private static void Reply(BunitContext c, DashboardResponse? data, int status = 200) => c.JSInterop.SetupModule("./dashboard-http.js")
        .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
        .SetResult(new(status, data is null ? null : JsonSerializer.SerializeToElement(data)));

    [Fact]
    public async Task Open_details_updates_relative_times_for_every_run_presentation()
    {
        await using var c = Context();
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        clock.SetUtcNow(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var attempt = new DashboardRun(Guid.NewGuid(), "head-sha", RunKind.Review, RunState.Running,
            StartedAt: now.AddMinutes(-5)) { UpdatedAt = now.AddMinutes(-2) };
        var success = new DashboardRun(Guid.NewGuid(), "head-sha", RunKind.Review, RunState.Completed,
            CompletedAt: now.AddMinutes(-10));
        var blocker = new DashboardRun(Guid.NewGuid(), "old-head", RunKind.Resolve, RunState.Running)
            { UpdatedAt = now.AddMinutes(-3) };
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = success,
            LatestAttempt = attempt, LatestPrAttempt = attempt, ActiveBlocker = blocker };
        var state = new DashboardRerunState();
        state.Reconcile(Data(row));
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row).Add(x => x.Reruns, state));
        Assert.Contains("2m ago", dialog.FindComponents<RunPresentation>().Single(p => p.Instance.Heading == "Latest PR attempt").Markup);

        await dialog.InvokeAsync(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            state.Reconcile(Data(row));
        });
        dialog.WaitForAssertion(() =>
        {
            var presentations = dialog.FindComponents<RunPresentation>();
            Assert.Equal(4, presentations.Count);
            Assert.All(presentations, p => Assert.Equal(clock.GetUtcNow(), p.Instance.Now));
            Assert.Contains("7m ago", presentations.Single(p => p.Instance.Heading == "Latest PR attempt").Markup);
        });

        var updated = attempt with { UpdatedAt = clock.GetUtcNow() };
        await dialog.InvokeAsync(() => state.Reconcile(Data(row with { LatestAttempt = updated, LatestPrAttempt = updated })));
        dialog.WaitForAssertion(() => Assert.Contains("Just now", dialog.FindComponents<RunPresentation>()
            .Single(p => p.Instance.Heading == "Latest PR attempt").Markup));
    }

    [Fact]
    public async Task Page_loads_after_interactive_render_and_filters_scoped_inventory()
    {
        await using var c = Context();
        Reply(c, Data(Row(), Row(1, "Other")));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("button.details-button").Count));
        Assert.Contains("Reviewed and Needs attention can overlap", page.Markup);
        Assert.Contains("Discovery disabled", page.Markup);
        Assert.Equal(3, page.FindComponents<RadzenDropDown<string>>().Count);
        page.Find("input[aria-label='Search pull requests']").Input("Other");
        Assert.Single(page.FindAll("button.details-button"));
        page.Find("input[aria-label='Search pull requests']").Input("absent");
        Assert.Contains("No matching pull requests", page.Markup);
        page.Find("input[aria-label='Search pull requests']").Input("");
        await page.InvokeAsync(() => page.FindComponents<RadzenDropDown<string>>().Single(x => x.Instance.Name == "Repository").Instance.ValueChanged.InvokeAsync("Other"));
        Assert.Single(page.FindAll("button.details-button"));
        await page.InvokeAsync(() => page.FindComponents<RadzenDropDown<string>>().Single(x => x.Instance.Name == "Status").Instance.ValueChanged.InvokeAsync("Reviewed"));
        Assert.Contains("No matching pull requests", page.Markup);
        await page.InvokeAsync(() => page.FindComponents<RadzenDropDown<string>>().Single(x => x.Instance.Name == "Status").Instance.ValueChanged.InvokeAsync("Unreviewed"));
        Assert.Single(page.FindAll("button.details-button"));
        await page.InvokeAsync(() => page.FindComponents<RadzenDropDown<string>>().Single(x => x.Instance.Name == "Project").Instance.ValueChanged.InvokeAsync("missing"));
        Assert.Contains("No matching pull requests", page.Markup);
        foreach (var dropdown in page.FindComponents<RadzenDropDown<string>>())
            await page.InvokeAsync(() => dropdown.Instance.ValueChanged.InvokeAsync((string)null!));
        Assert.Equal(2, page.FindAll("button.details-button").Count);
        Assert.All(c.JSInterop.Invocations.Where(i => i.Identifier == "send"), i => Assert.Equal("GET", i.Arguments[1]));
    }

    [Fact]
    public async Task Disposing_page_cancels_inflight_browser_request_and_never_starts_another_poll()
    {
        await using var c = Context();
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        var send = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        module.SetupVoid("abort", _ => true).SetVoidResult();
        var page = c.Render<Dashboard>();
        Assert.Contains("Loading pull requests", page.Markup);
        Assert.Single(module.Invocations["send"]);
        await page.InvokeAsync(async () => await page.Instance.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Single(module.Invocations["abort"]);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()))));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(module.Invocations["send"]);
    }

    [Fact]
    public async Task Refresh_keeps_paging_filters_and_live_details_then_explains_removed_pr()
    {
        await using var c = Context();
        var dialogs = c.Render<RadzenDialog>();
        var rows = Enumerable.Range(1, 18).Select(i => Row(i)).ToArray();
        var send = c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(rows))));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Equal(15, page.FindAll("button.details-button").Count));
        page.Find("input[aria-label='Search pull requests']").Input("Ada");
        await page.InvokeAsync(() => page.FindComponent<RadzenDataGrid<DashboardRow>>().Instance.GoToPage(1));
        page.Find("button.details-button").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Current head", dialogs.Markup));
        rows = rows.Select(r => r with { CurrentHeadReviewed = true }).ToArray();
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(rows))));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("button.details-button").Count));
        dialogs.WaitForAssertion(() => Assert.Contains("Reviewed", dialogs.Markup));
        Assert.Equal("Ada", page.Find("input[aria-label='Search pull requests']").GetAttribute("value"));
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data())));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("no longer in the retrieved inventory", dialogs.Markup));
        Assert.Contains("Reviewed", dialogs.Markup);
        Assert.True(dialogs.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Fake_time_stale_recovery_partial_and_auth_expiry_stop_refresh_without_post_replay()
    {
        await using var c = Context();
        var clock = (Microsoft.Extensions.Time.Testing.FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var send = c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()) with { IsComplete = false })));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Partial inventory", page.Markup));
        send.SetResult(new(503, null));
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(25)));
        page.WaitForAssertion(() => Assert.Contains("Last successful update", page.Markup));
        Assert.Contains("Partial inventory", page.Markup);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()))));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("previous snapshot", page.Markup));
        send.SetResult(new(401, null));
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(25)));
        page.WaitForAssertion(() => Assert.Contains("Session expired", page.Markup));
        Assert.True(page.Find("button[aria-label='Refresh dashboard']").HasAttribute("disabled"));
        Assert.True(page.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
        var count = c.JSInterop.Invocations.Count(i => i.Identifier == "send");
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromMinutes(2)));
        Assert.Equal(count, c.JSInterop.Invocations.Count(i => i.Identifier == "send"));
        Assert.All(c.JSInterop.Invocations.Where(i => i.Identifier == "send"), i => Assert.Equal("GET", i.Arguments[1]));
        Assert.Single(page.FindAll("[aria-live='polite']"));
    }

    [Theory]
    [InlineData(true, "No open pull requests")]
    [InlineData(false, "No pull requests retrieved")]
    public async Task Empty_and_partial_inventory_are_distinct(bool complete, string expected)
    {
        await using var c = Context();
        Reply(c, Data() with { IsComplete = complete, SourceWarnings = ["Project unavailable"], Summary = new(0, 0, 0, 0, !complete) });
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
        Assert.Contains("Project unavailable", page.Markup);
    }

    [Theory]
    [InlineData(401, "Session expired")]
    [InlineData(403, "Access denied")]
    [InlineData(429, "Too many requests")]
    [InlineData(503, "Dashboard unavailable")]
    [InlineData(500, "Could not load")]
    public async Task Initial_errors_are_actionable(int status, string expected)
    {
        await using var c = Context();
        Reply(c, null, status);
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
        Assert.NotEmpty(page.FindAll("button[aria-label='Refresh dashboard']"));
    }

    [Fact]
    public async Task Loading_refresh_stale_retention_and_details_do_not_submit_without_rerun_confirmation()
    {
        await using var c = Context();
        var dialogs = c.Render<RadzenDialog>();
        var send = c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        var page = c.Render<Dashboard>();
        Assert.Contains("Loading pull requests", page.Markup);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()))));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.details-button")));
        var refresh = page.Find("button[aria-label='Refresh dashboard']");
        send.SetResult(new(503, null));
        refresh.Click();
        page.WaitForAssertion(() => Assert.Contains("previous snapshot", page.Markup));
        Assert.Single(page.FindAll("button.details-button"));
        page.Find("button.details-button").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Current head", dialogs.Markup));
        Assert.False(dialogs.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
        dialogs.Find("button[aria-label='Close details']").Click();
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Current head", dialogs.Markup));
    }

    [Fact]
    public async Task Active_run_without_stage_is_indeterminate_and_blocker_keeps_its_own_identity()
    {
        await using var c = Context();
        var blocker = new DashboardRun(Guid.NewGuid(), "old-head", RunKind.Resolve, RunState.Running);
        var row = Row() with { ActiveBlocker = blocker };
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains(blocker.RunId.ToString(), dialog.Markup);
        Assert.Contains("Old-head Resolve", dialog.Markup);
        Assert.Contains("Waiting for worker", dialog.Markup);
        Assert.Equal(ProgressBarMode.Indeterminate, dialog.FindComponent<RadzenProgressBar>().Instance.Mode);
        Assert.NotEmpty(dialog.FindComponents<RadzenBadge>());
        Assert.Contains("Progress unknown", dialog.Markup);
    }

    [Fact]
    public async Task Grid_pages_full_inventory_and_search_resets_paging()
    {
        await using var c = Context();
        Reply(c, Data(Enumerable.Range(1, 18).Select(i => Row(i)).ToArray()));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Equal(15, page.FindAll("button.details-button").Count));
        await page.InvokeAsync(() => page.FindComponent<RadzenDataGrid<DashboardRow>>().Instance.GoToPage(1));
        Assert.Equal(3, page.FindAll("button.details-button").Count);
        page.Find("input[aria-label='Search pull requests']").Input("Ada");
        page.WaitForAssertion(() => Assert.Equal(15, page.FindAll("button.details-button").Count));
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Skipped)]
    public async Task Successful_coverage_survives_latest_unsuccessful_attempt_and_success_metadata_remains_visible(RunState state)
    {
        await using var c = Context();
        var successId = Guid.NewGuid();
        var success = new DashboardRun(successId, "head", RunKind.Review, RunState.Completed, ReviewFindings: 7);
        var latest = new DashboardRun(Guid.NewGuid(), "head", RunKind.Review, state);
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = success, LatestAttempt = latest, LatestPrAttempt = latest };
        Reply(c, Data(row));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Reviewed", page.Markup));
        Assert.Contains(state.ToString(), page.Markup);
        Assert.DoesNotContain("value=\"100\"", page.Markup);
        var details = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains(successId.ToString(), details.Markup);
        Assert.Contains("Review findings: 7", details.Markup);
    }

    [Theory]
    [InlineData(null, RunState.Failed, "Head not captured")]
    [InlineData("different-head", RunState.Completed, "Different head")]
    public async Task Grid_and_details_show_latest_pr_attempt_with_explicit_head_without_fabricating_coverage(
        string? head, RunState state, string attribution)
    {
        await using var c = Context();
        var attempt = new DashboardRun(Guid.NewGuid(), head, RunKind.Review, state,
            CurrentStage: "FetchPrContext", ProgressPercent: 0)
            { LoadedStages = ["FetchPrContext", "ReviewGate"] };
        var row = Row() with { LatestPrAttempt = attempt };
        Reply(c, Data(row) with { InventoryStale = true, InventoryRefreshing = true });
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains(attribution, page.Markup));
        Assert.Contains("Unreviewed", page.Markup);
        Assert.Contains("Latest PR attempt", page.Markup);
        Assert.Contains(state.ToString(), page.Markup);
        Assert.Contains("Source inventory is stale", page.Markup);
        Assert.Contains("Metadata refresh in progress", page.Markup);
        Assert.Contains("not current-head coverage", page.Markup);
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains(attribution, dialog.Markup);
        Assert.Contains(attempt.RunId.ToString(), dialog.Markup);
        Assert.Contains("Latest current-head attempt", dialog.Markup);
        Assert.Contains("No current-head review attempt", dialog.Markup);
        Assert.Equal(state, dialog.FindComponents<RunPresentation>().Single(r => r.Instance.Heading == "Latest PR attempt").Instance.Run!.State);
        Assert.Null(row.LastSuccessfulReview);
        Assert.Null(row.LatestAttempt);
    }

    [Fact]
    public async Task Details_show_ordered_stages_safe_text_blocker_and_unknown_history()
    {
        await using var c = Context();
        var run = new DashboardRun(Guid.NewGuid(), "old", RunKind.Resolve, RunState.Failed, CurrentStage: "ReviewGate", ProgressPercent: 33,
            ReviewFindings: 4) { LoadedStages = ["FetchPrContext", "ReviewGate", "PersistRun"] };
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = run with { State = RunState.Completed }, LatestAttempt = run, ActiveBlocker = run };
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains("Old-head", dialog.Markup);
        Assert.Contains("Review findings", dialog.Markup);
        Assert.Contains("Updated", dialog.Markup);
        Assert.Contains("Failed", dialog.FindAll("section[aria-label='Latest current-head attempt'] ol li")[1].TextContent);
        Assert.Contains("Not started", dialog.FindAll("section[aria-label='Latest current-head attempt'] ol li")[2].TextContent);
        Assert.Equal("noopener noreferrer", dialog.Find("a[target='_blank']").GetAttribute("rel"));
        dialog.Render(p => p.Add(x => x.Row, row with { LatestAttempt = run with { LoadedStages = [] }, ActiveBlocker = run with { HeadSha = null } }));
        Assert.Contains("Stage history unavailable", dialog.Markup);
        Assert.Contains("Unknown-head", dialog.Markup);
        dialog.Render(p => p.Add(x => x.Row, Row() with { LatestAttempt = null, PullRequest = Row().PullRequest with { BrowserUrl = "javascript:evil()", MetadataWarnings = ["<script>evil</script>"] } }));
        Assert.Empty(dialog.FindAll("a[target='_blank']"));
        Assert.Contains("&lt;script&gt;", dialog.Markup);
        Assert.Contains("No current-head review attempt", dialog.Markup);
    }
}
