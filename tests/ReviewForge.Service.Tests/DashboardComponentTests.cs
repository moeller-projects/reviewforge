using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using ReviewForge.Service.Components.Pages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardComponentTests
{
    private static DashboardRow Row() => new(new(new(new("org", "Project", "Repository", 1), new FakePullRequestSource().Pr,
        "main", "author", "Ada"), "Repository", "https://example.com/pr"), false, true);
    private static RunWorklistItem Item(int id = 1, string repo = "Repository", RunState state = RunState.Failed) => new(new("org", "Project", repo, id),
        new(Guid.NewGuid(), "head", RunKind.ReviewDraft, state, StartedAt: DateTimeOffset.UtcNow), Title: "Fix title", RepositoryName: repo);
    private static RunWorklistResponse Data(params RunWorklistItem[] items) => new(items, false, 0, 50, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, true, []);
    private static BunitContext Context()
    {
        var c = new BunitContext();
        c.Services.AddScoped<DashboardConfirmation>();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        return c;
    }

    [Fact]
    public async Task Landing_uses_browser_history_search_and_attention_toggle_without_publication()
    {
        await using var c = Context();
        var send = c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Item(), Item(2, "Closed repo", RunState.Completed)))));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".worklist-pr-toggle")));
        Assert.Equal("/dashboard/start", page.Find("a.dashboard-start").GetAttribute("href"));
        Assert.Contains("Runs needing attention", page.Markup);
        Assert.DoesNotContain("Current-head coverage", page.Markup);
        page.Find("button[aria-label='Show all recent runs']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".worklist-pr-toggle").Count));
        page.Find("input[aria-label='Search runs']").Input("Closed repo");
        Assert.Single(page.FindAll(".worklist-pr-toggle"));
        page.Find("input[aria-label='Search runs']").Input("<script>");
        Assert.Empty(page.FindAll(".worklist-pr-toggle"));
        Assert.Empty(page.FindAll("script"));
        Assert.All(c.JSInterop.Invocations.Where(i => i.Identifier == "send"), i => Assert.Equal("GET", i.Arguments[1]));
    }

    [Fact]
    public async Task Paging_refresh_preserves_loaded_history_filter_and_manual_expansion()
    {
        await using var c = Context();
        var first = Item();
        var second = Item(2);
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        var head = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("offset=0&"));
        head.SetResult(new(200, JsonSerializer.SerializeToElement(Data(first) with { HasMore = true })));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("offset=50&"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(second) with { Offset = 50 })));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".worklist-pr-toggle")));
        page.Find(".worklist-pr-toggle").Click();
        page.Find("button[aria-label='Load more runs']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".worklist-pr-toggle").Count));
        head.SetResult(new(200, JsonSerializer.SerializeToElement(Data(first with { Title = "Changed" }) with { HasMore = true })));
        page.Find("input[aria-label='Search runs']").Input("Project");
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.Contains("Changed", page.Markup));
        Assert.Equal(2, page.FindAll(".worklist-pr-toggle").Count);
        Assert.Equal("false", page.FindAll(".worklist-pr-toggle").Single(b => b.TextContent.Contains("Changed")).GetAttribute("aria-expanded"));
        Assert.Equal("Project", page.Find("input[aria-label='Search runs']").GetAttribute("value"));
    }

    [Fact]
    public async Task Timer_refresh_retains_displaced_history_and_a_later_page_failure_is_atomic()
    {
        await using var c = Context();
        var clock = (FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var first = Item();
        var second = Item(2);
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        var head = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("offset=0&"));
        var tail = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("offset=50&"));
        head.SetResult(new(200, JsonSerializer.SerializeToElement(Data(first) with { HasMore = true })));
        tail.SetResult(new(200, JsonSerializer.SerializeToElement(Data(second) with { Offset = 50 })));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".worklist-pr-toggle")));
        page.Find("button[aria-label='Load more runs']").Click();
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".worklist-pr-toggle").Count));
        head.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Item(3)) with { HasMore = true })));
        tail.SetResult(new(503, null));
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(25)));
        page.WaitForAssertion(() => Assert.Contains("previous snapshot", page.Markup));
        Assert.Equal(2, page.FindAll(".worklist-pr-toggle").Count);
        tail.SetResult(new(200, JsonSerializer.SerializeToElement(Data(second) with { Offset = 50 })));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".worklist-pr-toggle").Count));
        var warning = page.Find(".retained-history-warning");
        Assert.Contains("outside the refreshed window", warning.TextContent);
        Assert.Null(warning.Closest("details"));
    }

    [Fact]
    public async Task Attention_view_queries_global_attention_before_paging_and_switching_replaces_the_window()
    {
        await using var c = Context();
        var oldPending = Item(42, "Old pending draft");
        var recent = Item(99, "Recent completed", RunState.Completed);
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("attentionOnly=true"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(oldPending))));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => ((string)i.Arguments[2]!).Contains("attentionOnly=false"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(recent))));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Old pending draft", page.Markup));
        page.Find("button[aria-label='Show all recent runs']").Click();
        page.WaitForAssertion(() => Assert.Contains("Recent completed", page.Markup));
        Assert.DoesNotContain("Old pending draft", page.Markup);
        page.Find("button[aria-label='Show runs needing attention']").Click();
        page.WaitForAssertion(() => Assert.Contains("Old pending draft", page.Markup));
        Assert.DoesNotContain("Recent completed", page.Markup);
    }

    [Fact]
    public async Task Metadata_failure_previous_snapshot_and_auth_expiry_are_safe()
    {
        await using var c = Context();
        var clock = (FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var send = c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data(Item()) with { InventoryStale = true, InventoryComplete = false, SourceWarnings = ["Source unavailable"] })));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Source unavailable", page.Markup));
        Assert.Contains("Findings unavailable", page.Markup);
        Assert.Contains("Partial inventory", page.Markup);
        send.SetResult(new(503, null));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.Contains("previous snapshot", page.Markup));
        Assert.Single(page.FindAll(".worklist-pr-toggle"));
        send.SetResult(new(401, null));
        page.Find("button[aria-label='Refresh dashboard']").Click();
        page.WaitForAssertion(() => Assert.Contains("Session expired", page.Markup));
        var count = c.JSInterop.Invocations.Count(i => i.Identifier == "send");
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromMinutes(2)));
        Assert.Equal(count, c.JSInterop.Invocations.Count(i => i.Identifier == "send"));
        Assert.True(page.Find("button[aria-label='Refresh dashboard']").HasAttribute("disabled"));
        Assert.Single(page.FindAll("[aria-live='polite']"));
    }

    [Theory]
    [InlineData(403, "Access denied")]
    [InlineData(429, "Too many requests")]
    [InlineData(503, "Dashboard unavailable")]
    [InlineData(500, "Could not load")]
    public async Task Initial_errors_are_actionable(int status, string expected)
    {
        await using var c = Context();
        c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true).SetResult(new(status, null));
        var page = c.Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains(expected, page.Markup));
    }

    [Fact]
    public async Task Disposal_cancels_browser_request_and_polling()
    {
        await using var c = Context();
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        var send = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true);
        module.SetupVoid("abort", _ => true).SetVoidResult();
        var page = c.Render<Dashboard>();
        Assert.Contains("Loading runs", page.Markup);
        await page.InvokeAsync(async () => await page.Instance.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Single(module.Invocations["abort"]);
        send.SetResult(new(200, JsonSerializer.SerializeToElement(Data())));
        ((FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromMinutes(1));
        Assert.Single(module.Invocations["send"]);
    }

    // Legacy standalone details retain their own regression coverage; the landing page no longer renders them.
    [Theory]
    [InlineData("head-sha", "Current head")]
    [InlineData("other-head", "Different head; not current-head coverage.")]
    [InlineData(null, "Head not captured; not current-head coverage.")]
    public async Task Standalone_details_bind_actual_head(string? head, string expected)
    {
        await using var c = Context();
        var row = Row() with { LatestPrAttempt = new(Guid.NewGuid(), head, RunKind.Review, RunState.Completed) };
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        var presentation = dialog.FindComponents<RunPresentation>().Single(p => p.Instance.Heading == "Latest PR attempt");
        Assert.Equal(row.PullRequest.Candidate.Pr.SourceCommitSha, presentation.Instance.CurrentHead);
        Assert.Contains(expected, presentation.Markup);
    }

    [Fact]
    public async Task Standalone_details_preserve_stages_safe_text_blocker_and_unknown_history()
    {
        await using var c = Context();
        var run = new DashboardRun(Guid.NewGuid(), "old", RunKind.Resolve, RunState.Failed, CurrentStage: "ReviewGate", ProgressPercent: 33,
            ReviewFindings: 4) { LoadedStages = ["FetchPrContext", "ReviewGate", "PersistRun"] };
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = run with { State = RunState.Completed }, LatestAttempt = run, ActiveBlocker = run };
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains("Old-head", dialog.Markup);
        Assert.Contains("Review findings", dialog.Markup);
        Assert.Contains("Failed", dialog.FindAll("section[aria-label='Latest current-head attempt'] ol li")[1].TextContent);
        Assert.Contains("Not started", dialog.FindAll("section[aria-label='Latest current-head attempt'] ol li")[2].TextContent);
        Assert.Equal("noopener noreferrer", dialog.Find("a[target='_blank']").GetAttribute("rel"));
        dialog.Render(p => p.Add(x => x.Row, row with { LatestAttempt = run with { LoadedStages = [] }, ActiveBlocker = run with { HeadSha = null } }));
        Assert.Contains("Stage history unavailable", dialog.Markup);
        Assert.Contains("Unknown-head", dialog.Markup);
        dialog.Render(p => p.Add(x => x.Row, Row() with { PullRequest = Row().PullRequest with { BrowserUrl = "javascript:evil()", MetadataWarnings = ["<script>evil</script>"] } }));
        Assert.Empty(dialog.FindAll("a[target='_blank']"));
        Assert.Contains("&lt;script&gt;", dialog.Markup);
        Assert.Contains("No current-head review attempt", dialog.Markup);
    }

    [Fact]
    public async Task Standalone_details_update_relative_times_and_unknown_progress()
    {
        await using var c = Context();
        var clock = (FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var run = new DashboardRun(Guid.NewGuid(), "old-head", RunKind.Resolve, RunState.Running) { UpdatedAt = clock.GetUtcNow().AddMinutes(-3) };
        var row = Row() with { LatestPrAttempt = run, ActiveBlocker = run };
        var state = new DashboardRerunState();
        DashboardResponse Snapshot() => new(clock.GetUtcNow(), clock.GetUtcNow(), true, [], [row]);
        state.Reconcile(Snapshot());
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row).Add(x => x.Reruns, state));
        Assert.Contains(run.RunId.ToString(), dialog.Markup);
        Assert.Contains("Waiting for worker", dialog.Markup);
        Assert.Contains("Progress unknown", dialog.Markup);
        Assert.NotEmpty(dialog.FindAll("progress"));
        Assert.All(dialog.FindAll("progress"), p => Assert.False(p.HasAttribute("value")));
        await dialog.InvokeAsync(() => { clock.Advance(TimeSpan.FromMinutes(5)); state.Reconcile(Snapshot()); });
        dialog.WaitForAssertion(() => Assert.Contains("8m ago", dialog.Markup));
    }
}
