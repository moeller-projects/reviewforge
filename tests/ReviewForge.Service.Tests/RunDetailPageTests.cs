using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Pages;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunDetailPageTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static RunFindingDto Finding(string id = "finding", FindingDecisionState state = FindingDecisionState.Approved,
        bool published = false, bool eligible = true) => new(id,
        new("rule", "<script>title</script>", "high", "bug", "Claim", "<script>snippet</script>", "Suggestion", new("file.cs", 1, 2), false),
        eligible ? new("rule", "Accepted", "high", "bug", "Accepted claim", "snippet", null, new("file.cs", 2, 3), false) : null,
        new(eligible ? FindingValidationStatus.Accepted : FindingValidationStatus.Rejected, "validation reason", "validator",
            eligible ? FindingVerificationStatus.Confirmed : FindingVerificationStatus.Rejected, "verification reason", "lightweight"),
        false, new(Id, id, "head", state, "human reason", "dashboard-session", DateTimeOffset.UtcNow, 3), 3, published, published ? 12 : null, eligible, true);
    private static RunDetailsResponse Details(params RunFindingDto[] findings) => new(
        new(Id, new("org", "project", "repo", 1), RunState.Completed, null, DateTimeOffset.UtcNow, RunKind.ReviewDraft),
        "head", true, null, findings) { Source = new("PR title", "Repository", "https://example.com/pr?x=1", "head", DateTimeOffset.UtcNow, false, null) };
    private static BunitContext Context(RunDetailsResponse details, int status = 200)
    {
        var c = new BunitContext();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton(TimeProvider.System);
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/details"))
            .SetResult(new(status, JsonSerializer.SerializeToElement(details)));
        return c;
    }
    [Fact]
    public async Task Deep_link_defaults_to_findings_escapes_payload_and_filters_rejected_and_posted()
    {
        await using var c = Context(Details(Finding(), Finding("rejected", FindingDecisionState.Rejected, eligible: false), Finding("posted", published: true)));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("article.finding-card")));
        Assert.DoesNotContain("<script>", page.Markup);
        Assert.Contains("&lt;script&gt;", page.Markup);
        Assert.Contains("validation reason", page.Markup);
        page.Find("select[aria-label='Finding filter']").Change("pipeline-rejected");
        Assert.Contains("human reason", page.Markup);
        Assert.Single(page.FindAll("article.finding-card"));
        page.Find("select[aria-label='Finding filter']").Change("posted");
        Assert.Contains("discussionId=12", page.Markup);
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }
    [Fact]
    public async Task Publication_requires_nonempty_selection_and_separate_frozen_confirmation_and_shows_partial_outcomes()
    {
        await using var c = Context(Details(Finding()));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(true, null, [new("finding", PublishFindingStatus.Unknown)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[aria-label='Select finding for publication']")));
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        page.Find("input[type=checkbox]").Change(true);
        page.Find("button.post-selected").Click();
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        Assert.Contains("head", page.Find("dialog[aria-label='Confirm publication']").TextContent);
        page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Contains("Unknown", page.Markup));
        Assert.Contains("reconciliation", page.Markup);
        var call = Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        var request = Assert.IsType<PublishSelectedDraftRequest>(call.Arguments[3]);
        Assert.Equal("head", request.HeadSha);
        Assert.Equal(new PublishFindingSelection("finding", 3), Assert.Single(request.Selections!));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_dialog_preserves_focus_return_target_when_trigger_is_disabled(bool confirm)
    {
        await using var c = Context(Details(Finding()));
        var dialog = c.JSInterop.SetupModule("./dashboard-dialog.js");
        dialog.SetupVoid("open", _ => true).SetVoidResult();
        dialog.SetupVoid("close", _ => true).SetVoidResult();
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(true, null,
                [new("finding", PublishFindingStatus.Posted)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[type=checkbox]")));
        page.Find("input[type=checkbox]").Change(true);
        var target = page.Find(".publication-action");
        var returnId = target.Id;
        Assert.False(string.IsNullOrWhiteSpace(returnId));
        Assert.Equal("-1", target.GetAttribute("tabindex"));
        page.Find("button.post-selected").Click();
        page.WaitForAssertion(() => Assert.Single(dialog.Invocations["open"]));
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        Assert.Equal(returnId, Assert.Single(dialog.Invocations["open"]).Arguments[1]);
        page.Find(confirm ? "button.confirm-publication" : "[data-dialog-cancel]").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("dialog")));
        page.WaitForAssertion(() => Assert.Single(dialog.Invocations["close"]));
        target = page.Find(".publication-action");
        Assert.Equal(returnId, target.Id);
        Assert.False(target.HasAttribute("disabled"));
        Assert.Equal("-1", target.GetAttribute("tabindex"));
    }

    [Fact]
    public async Task Unknown_publication_is_labeled_honestly_and_explicit_reconciliation_only_reads_external_state()
    {
        await using var c = new BunitContext();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton(TimeProvider.System);
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        var get = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/details"));
        get.SetResult(new(200, JsonSerializer.SerializeToElement(Details(Finding()))));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(false, "publication-unknown", [new("finding", PublishFindingStatus.Unknown)]))));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/reconcile"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(true, null, [new("finding", PublishFindingStatus.AlreadyPosted, 12)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[type=checkbox]")));
        page.Find("select[aria-label='Finding filter']").Change("all");
        page.Find("input[type=checkbox]").Change(true);
        page.Find("button.post-selected").Click();
        page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Contains("Publication: Unknown", page.Find("article.finding-card").TextContent));
        Assert.True(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        get.SetResult(new(200, JsonSerializer.SerializeToElement(Details(Finding(published: true)))));
        page.Find("button.reconcile-publication").Click();
        page.WaitForAssertion(() => Assert.Contains("Publication: Posted", page.Find("article.finding-card").TextContent));
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"));
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/reconcile"));
    }

    [Fact]
    public async Task Reloaded_durable_unknown_stays_blocked_and_offers_explicit_reconciliation_without_posting()
    {
        await using var c = Context(Details(Finding() with { Published = null, PublicationUnknown = true }));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("Publication: Unknown", page.Find("article.finding-card").TextContent));
        Assert.True(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.Single(page.FindAll("button.reconcile-publication"));
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }

    [Fact]
    public async Task Known_changed_head_blocks_post_but_unknown_legacy_remains_explicit()
    {
        await using var c = Context(Details(Finding()) with { Source = new(null, null, "javascript:alert(1)", "changed", null, false, null) });
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("new draft", page.Markup));
        Assert.True(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.DoesNotContain("javascript:", page.Markup);
        await using var legacy = Context(Details() with { ReviewedHeadSha = null, ObservationsAvailable = false, Source = null });
        var old = legacy.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        old.WaitForAssertion(() => Assert.Contains("unavailable", old.Markup));
    }
    [Fact]
    public async Task Selection_is_limited_to_approved_technically_eligible_explicitly_unpublished_findings()
    {
        var rows = new[] { Finding(), Finding("pending", FindingDecisionState.Pending), Finding("deferred", FindingDecisionState.Deferred),
            Finding("rejected", FindingDecisionState.Rejected), Finding("ineligible", eligible: false), Finding("posted", published: true), Finding("unknown") with { Published = null } };
        await using var c = Context(Details(rows));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("select[aria-label='Finding filter']")));
        page.Find("select[aria-label='Finding filter']").Change("all");
        var checks = page.FindAll("input[type=checkbox]");
        Assert.Equal(7, checks.Count);
        Assert.False(checks[0].HasAttribute("disabled"));
        Assert.All(checks.Skip(1), box => Assert.True(box.HasAttribute("disabled")));
        page.Find("select[aria-label='Finding filter']").Change("human-rejected");
        Assert.Single(page.FindAll("article.finding-card"));
        page.Find("select[aria-label='Finding filter']").Change("history");
        Assert.Equal(7, page.FindAll("article.finding-card").Count);
    }
    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:password@example.com/pr", false)]
    [InlineData("https://example.com/pr?discussionId=old&x=1#old", true)]
    public async Task Thread_links_reject_unsafe_schemes_and_credentials_and_replace_discussion_query(string url, bool linked)
    {
        var details = Details(Finding(published: true));
        await using var c = Context(details with { Source = details.Source! with { BrowserUrl = url } });
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("select[aria-label='Finding filter']")));
        page.Find("select[aria-label='Finding filter']").Change("posted");
        var links = page.FindAll("article a");
        Assert.Equal(linked ? 1 : 0, links.Count);
        if (linked)
        {
            Assert.Equal("https://example.com/pr?x=1&discussionId=12", links[0].GetAttribute("href"));
            Assert.Equal("noopener noreferrer", links[0].GetAttribute("rel"));
        }
    }
    [Fact]
    public async Task History_is_lazy_and_decision_conflicts_refresh_without_overwrite()
    {
        await using var c = Context(Details(Finding()));
        var module = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/history?findingId=finding"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new FindingDecisionHistoryResponse([]))));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions"))
            .SetResult(new(409, JsonSerializer.SerializeToElement(new FindingDecisionResponse(FindingDecisionStatus.Conflict, 4, null))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.finding-history")));
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && i.Arguments[2]?.ToString()?.Contains("history?") == true);
        page.Find("button.finding-history").Click();
        page.WaitForAssertion(() => Assert.Contains("No saved decisions", page.Markup));
        page.Find("textarea").Input("reason");
        page.Find(".finding-decision-editor button").Click();
        page.WaitForAssertion(() => Assert.Contains("changed", page.Markup));
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        Assert.True(c.JSInterop.Invocations.Count(i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/details")) >= 2);
    }
    [Theory]
    [InlineData(PublishFindingStatus.Posted)]
    [InlineData(PublishFindingStatus.AlreadyPosted)]
    [InlineData(PublishFindingStatus.Failed)]
    [InlineData(PublishFindingStatus.NotAttempted)]
    public async Task Typed_publication_outcomes_do_not_trigger_automatic_retry(PublishFindingStatus outcome)
    {
        await using var c = Context(Details(Finding()));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(true, null, [new("finding", outcome)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("input[type=checkbox]")));
        page.Find("input[type=checkbox]").Change(true);
        page.Find("button.post-selected").Click();
        Assert.True(page.Find(".finding-decision-editor button").HasAttribute("disabled"));
        page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Contains(outcome.ToString(), page.Find("section[aria-label='Publication outcomes']").TextContent));
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }
    [Fact]
    public async Task Save_sends_reason_and_expected_version_but_never_publication()
    {
        await using var c = Context(Details(Finding()));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new FindingDecisionResponse(FindingDecisionStatus.Saved, 4, null))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("textarea")));
        page.Find("select[aria-label='Finding decision']").Change("Rejected");
        page.Find("textarea").Input("");
        Assert.True(page.Find(".finding-decision-editor button").HasAttribute("disabled"));
        page.Find("textarea").Input(" supported objection ");
        page.Find(".finding-decision-editor button").Click();
        page.WaitForAssertion(() => Assert.Contains("Decision saved", page.Markup));
        var call = Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        var request = Assert.IsType<SetFindingDecisionRequest>(call.Arguments[3]);
        Assert.Equal(new SetFindingDecisionRequest("finding", "head", FindingDecisionState.Rejected, "supported objection", 3), request);
    }
    [Fact]
    public async Task Queued_shell_polls_coalesced_and_disposal_cancels_browser_request()
    {
        var details = Details() with { Status = Details().Status with { State = RunState.Queued }, ReviewedHeadSha = null };
        await using var c = Context(details);
        var clock = new FakeTimeProvider();
        c.Services.AddSingleton<TimeProvider>(clock);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("Queued", page.Markup));
        Assert.Empty(page.FindAll("article.finding-card"));
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromSeconds(3)));
        page.WaitForAssertion(() => Assert.Equal(2, c.JSInterop.Invocations.Count(i => i.Identifier == "send")));
        var next = Guid.NewGuid();
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{next:D}/details"));
        page.Render(p => p.Add(x => x.RunId, next));
        page.WaitForAssertion(() => Assert.Contains("Loading run", page.Markup));
        await page.InvokeAsync(() => page.Instance.DisposeAsync().AsTask());
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "abort");
    }
    [Fact]
    public async Task Late_previous_route_response_cannot_replace_new_run_or_resume_old_polling()
    {
        await using var c = Context(Details(Finding()));
        var old = Guid.NewGuid();
        var pending = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{old:D}/details"));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, old));
        page.WaitForAssertion(() => Assert.Contains("Loading run", page.Markup));
        page.Render(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("article.finding-card")));
        pending.SetResult(new(200, JsonSerializer.SerializeToElement(Details() with { ReviewedHeadSha = "late-old-head" })));
        Assert.DoesNotContain("late-old-head", page.Markup);
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "abort");
    }
    [Theory]
    [InlineData(401, "Session expired")]
    [InlineData(404, "Run not found")]
    public async Task Unavailable_deep_links_have_no_mutation_controls(int status, string message)
    {
        await using var c = Context(Details(Finding()), status);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains(message, page.Markup));
        Assert.Empty(page.FindAll("article.finding-card"));
    }
    [Fact]
    public async Task Execution_and_context_show_available_plan_and_times_without_inventing_diagnostics()
    {
        var details = Details() with { Status = Details().Status with { LoadedStages = ["ReviewGate", "Reasoning"], CurrentStage = "Reasoning" }, Counts = new(2, 1, 1, 0, 0) };
        await using var c = Context(details);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("nav button").Count));
        page.FindAll("nav button").Single(b => b.TextContent == "Execution").Click();
        Assert.Contains("Reasoning", page.Markup);
        Assert.Contains("Durable execution history unavailable", page.Markup);
        page.FindAll("nav button").Single(b => b.TextContent == "Context").Click();
        Assert.Contains("Candidates 2", page.Markup);
        Assert.Contains("Run context provenance unavailable", page.Markup);
    }
    [Fact]
    public async Task Unknown_current_head_warns_without_replacing_the_authoritative_publication_check()
    {
        await using var c = Context(Details(Finding()) with { Source = new(null, null, null, null, null, true, "Metadata unavailable") });
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("Current head is unknown", page.Markup));
        Assert.False(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        page.Find("input[type=checkbox]").Change(true);
        Assert.False(page.Find("button.post-selected").HasAttribute("disabled"));
    }
    [Fact]
    public async Task Authentication_expiry_stops_polling_and_disables_decisions()
    {
        await using var c = Context(Details(Finding()), 401);
        var clock = new FakeTimeProvider();
        c.Services.AddSingleton<TimeProvider>(clock);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("Session expired", page.Markup));
        await page.InvokeAsync(() => clock.Advance(TimeSpan.FromMinutes(1)));
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send");
        Assert.True(page.Find("header button").HasAttribute("disabled"));
    }
    [Fact]
    public async Task Route_change_clears_previous_snapshot_and_ignores_late_previous_response()
    {
        await using var c = Context(Details(Finding()));
        var next = Guid.NewGuid();
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{next:D}/details"))
            .SetResult(new(404, null));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("article.finding-card")));
        page.Render(p => p.Add(x => x.RunId, next));
        page.WaitForAssertion(() => Assert.Contains("Run not found", page.Markup));
        Assert.Empty(page.FindAll("article.finding-card"));
    }
}
