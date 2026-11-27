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
    [Theory]
    [InlineData("execution", "Execution")]
    [InlineData("invalid", "Findings")]
    public async Task Worklist_deep_link_selects_only_a_section_and_never_mutates(string section, string expected)
    {
        await using var c = Context(Details(Finding()));
        c.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/dashboard/runs/{Id}?section={section}");
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".run-summary")));
        Assert.Equal(expected, Assert.Single(page.FindAll(".run-sections button[aria-pressed='true']")).TextContent);
        Assert.All(c.JSInterop.Invocations.Where(i => i.Identifier == "send"), i => Assert.Equal("GET", i.Arguments[1]));
    }

    [Theory]
    [InlineData(" shared ", "", true, "shared")]
    [InlineData("", " override ", true, "override")]
    [InlineData("  ", "  ", false, "")]
    [InlineData("shared", "   ", true, "shared")]
    [InlineData("long", "", false, "")]
    [InlineData("", "long", false, "")]
    [InlineData("boundary", "", true, "boundary")]
    public async Task Rejection_validates_trimmed_effective_reasons(string shared, string individual, bool valid, string expected)
    {
        await using var c = Context(Details(Finding("a")));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new BatchFindingDecisionResponse(null, [new("a", BatchFindingDecisionStatus.Saved, 4)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true); page.Find("[data-command='batch-reject']").Click();
        page.Find("[aria-label='Shared rejection reason']").Input(shared == "long" ? new string('x', 2001) : shared == "boundary" ? " " + new string('x', 2000) + " " : shared);
        page.Find("[aria-label='Override reason for a']").Input(individual == "long" ? new string('x', 2001) : individual);
        Assert.Equal(!valid, page.Find("[data-rejection-save]").HasAttribute("disabled"));
        if (!valid) return;
        page.Find("[data-rejection-save]").Click();
        page.WaitForAssertion(() => Assert.Contains("Saved", page.Markup));
        var request = Assert.IsType<BatchFindingDecisionRequest>(Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch")).Arguments[3]);
        Assert.Equal(expected == "boundary" ? new string('x', 2000) : expected, Assert.Single(request.Selections).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejection_navigation_warning_remains_active_in_other_run_sections(bool discard)
    {
        await using var c = Context(Details(Finding("a")));
        c.JSInterop.Setup<bool>("confirm", _ => true).SetResult(discard);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true); page.Find("[data-command='batch-reject']").Click();
        page.Find("[aria-label='Shared rejection reason']").Input("keep this");
        page.FindAll(".run-sections button")[1].Click();
        var nav = c.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var previous = nav.Uri;
        await page.InvokeAsync(() => nav.NavigateTo("/dashboard"));
        page.WaitForAssertion(() => Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "confirm"));
        Assert.Equal(discard ? "http://localhost/dashboard" : previous, nav.Uri);
        page.FindAll(".run-sections button")[0].Click();
        Assert.Contains("keep this", page.Find("[data-rejection-entry]").TextContent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_registers_direct_navigation_warning_as_soon_as_reasons_change(bool useOverride)
    {
        await using var c = Context(Details(Finding("a")));
        c.JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true);
        page.Find("[data-command='batch-reject']").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindComponents<Microsoft.AspNetCore.Components.Routing.NavigationLock>()));
        Assert.False(page.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>().Instance.ConfirmExternalNavigation);
        page.Find(useOverride ? "[aria-label='Override reason for a']" : "[aria-label='Shared rejection reason']").Input("retain this reason");
        page.WaitForAssertion(() => Assert.True(page.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>().Instance.ConfirmExternalNavigation));
        var nav = c.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var previous = nav.Uri;
        await page.InvokeAsync(() => nav.NavigateTo("/dashboard"));
        page.WaitForAssertion(() => Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "confirm"));
        Assert.Equal(previous, nav.Uri);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Rejection_failure_allows_warned_dismissal_but_submission_does_not(int status)
    {
        await using var c = Context(Details(Finding("a")));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        var response = http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true);
        page.Find("[data-command='batch-reject']").Click();
        page.Find("[aria-label='Shared rejection reason']").Input("retain this reason");
        page.Find("[data-rejection-save]").Click();
        page.WaitForAssertion(() => Assert.True(page.Find("[data-rejection-dismiss]").HasAttribute("disabled")));
        response.SetResult(new(status, null));
        page.WaitForAssertion(() => Assert.Contains("Unknown", page.Find("[aria-label='Rejection outcomes']").TextContent));
        Assert.True(page.Find("[data-rejection-save]").HasAttribute("disabled"));
        Assert.False(page.Find("[data-rejection-dismiss]").HasAttribute("disabled"));
        page.Find("[data-rejection-dismiss]").Click();
        page.WaitForAssertion(() => Assert.Contains("Discard unsaved rejection reasons?", page.Markup));
        page.Find("[data-rejection-discard]").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("dialog")));
        Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"));
    }

    [Fact]
    public async Task Rejection_freezes_membership_preserves_drafts_on_reads_and_warns_before_discard()
    {
        await using var c = Context(Details(Finding("a"), Finding("b")));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        page.Find(".finding-row input").Change(true); page.Find("[data-command='batch-reject']").Click();
        page.Find("[aria-label='Shared rejection reason']").Input("keep this");
        page.FindAll(".finding-row input")[1].Change(true);
        page.Find("input[aria-label='Search loaded findings']").Input("unmatched");
        page.Find(".dashboard-header button").Click();
        page.WaitForAssertion(() => Assert.False(page.Find(".dashboard-header button").HasAttribute("disabled")));
        page.Find("[data-rejection-dismiss]").Click();
        Assert.Contains("Discard unsaved rejection reasons?", page.Markup);
        page.Find("[data-rejection-keep]").Click();
        Assert.Single(page.FindAll("[data-rejection-entry]"));
        Assert.Contains("keep this", page.Find("[data-rejection-entry]").TextContent);
        page.Find("[data-rejection-dismiss]").Click(); page.Find("[data-rejection-discard]").Click();
        Assert.Empty(page.FindAll("[data-rejection-entry]"));
    }

    [Fact]
    public async Task Rejection_unknown_outcome_retains_reasons_without_replay()
    {
        await using var c = Context(Details(Finding("a")));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch")).SetResult(new(500, null));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true); page.Find("[data-command='batch-reject']").Click();
        page.Find("[aria-label='Shared rejection reason']").Input("do not lose");
        page.Find("[data-rejection-save]").Click();
        page.WaitForAssertion(() => Assert.Contains("Unknown", page.Find("[aria-label='Rejection outcomes']").TextContent));
        Assert.Contains("do not lose", page.Find("[data-rejection-entry]").TextContent);
        Assert.True(page.Find("[data-rejection-save]").HasAttribute("disabled"));
        Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"));
    }

    [Fact]
    public async Task Batch_rejection_previews_effective_reasons_and_preserves_only_unresolved_entries()
    {
        await using var c = Context(Details(Finding("a"), Finding("b")));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new BatchFindingDecisionResponse(null,
                [new("a", BatchFindingDecisionStatus.Saved, 4), new("b", BatchFindingDecisionStatus.Conflict, 4)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        page.FindAll(".queue-selection button")[0].Click();
        page.Find("[data-command='batch-reject']").Click();
        Assert.True(page.Find("[data-rejection-save]").HasAttribute("disabled"));
        page.Find("[aria-label='Shared rejection reason']").Input(" shared reason ");
        page.Find("[aria-label='Override reason for b']").Input(" individual reason ");
        Assert.Contains("shared reason", page.Find("[data-rejection-entry='a']").TextContent);
        Assert.Contains("individual reason", page.Find("[data-rejection-entry='b']").TextContent);
        page.Find("[data-rejection-save]").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-rejection-entry]")));
        Assert.Equal("b", page.Find("[data-rejection-entry]").GetAttribute("data-rejection-entry"));
        Assert.Equal(" individual reason ", page.Find("[aria-label='Override reason for b']").GetAttribute("value") ?? page.Find("[aria-label='Override reason for b']").TextContent);
        Assert.Contains("Saved", page.Find("[aria-label='Rejection outcomes']").TextContent);
        Assert.True(page.Find("[data-rejection-save]").HasAttribute("disabled"));
        var request = Assert.IsType<BatchFindingDecisionRequest>(Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch")).Arguments[3]);
        Assert.Equal(new[] { "shared reason", "individual reason" }, request.Selections.Select(s => s.Reason));
        Assert.All(request.Selections, s => { Assert.Equal(3, s.ExpectedVersion); Assert.Equal(FindingDecisionState.Rejected, s.State); });
        Assert.DoesNotContain(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"));
    }

    [Theory]
    [InlineData(RunKind.Review, RunState.Completed)]
    [InlineData(RunKind.Resolve, RunState.Completed)]
    [InlineData(RunKind.ReviewDraft, RunState.Running)]
    [InlineData(RunKind.ReviewDraft, RunState.Failed)]
    public async Task Noneditable_runs_cannot_submit_bulk_even_through_a_stale_callback(RunKind kind, RunState runState)
    {
        var details = Details(Finding("a") with { CanDecide = false }); details = details with { Status = details.Status with { Kind = kind, State = runState } };
        await using var c = Context(details); var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true); Assert.True(page.Find("[data-command='batch-approve']").HasAttribute("disabled"));
        await page.InvokeAsync(() => page.FindComponent<ReviewForge.Service.Components.Dashboard.FindingQueue>().Instance.OnBatch.InvokeAsync(new BatchFindingDecisionRequest("head", [new("a", 3, FindingDecisionState.Approved, null)])));
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }

    [Fact]
    public async Task Bulk_freezes_explicit_versions_including_hidden_selection_retains_unresolved_and_never_advances()
    {
        await using var c = Context(Details(Finding("a", FindingDecisionState.Pending), Finding("b", FindingDecisionState.Deferred), Finding("c", FindingDecisionState.Deferred)));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new BatchFindingDecisionResponse("storage-failure",
                [new("a", BatchFindingDecisionStatus.Saved, 4), new("b", BatchFindingDecisionStatus.Conflict, 4), new("c", BatchFindingDecisionStatus.Unknown)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".finding-row").Count));
        page.FindAll(".queue-selection button")[0].Click();
        page.Find("select[aria-label='Decision filter']").Change("Pending");
        page.Find("[data-command='batch-approve']").Click();
        page.WaitForAssertion(() => Assert.Contains("2 selected · 2 hidden", page.Markup));
        var request = Assert.IsType<BatchFindingDecisionRequest>(Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch")).Arguments[3]);
        Assert.Equal(new[] { "a", "b", "c" }, request.Selections.Select(i => i.FindingId));
        Assert.All(request.Selections, i => { Assert.Equal(3, i.ExpectedVersion); Assert.Equal(FindingDecisionState.Approved, i.State); });
        Assert.Contains("Saved", page.Find("[aria-label='Batch decision outcomes']").TextContent);
        Assert.Contains("Conflict", page.Markup); Assert.Contains("Unknown", page.Markup);
        Assert.Equal("a", page.Find("[aria-current='true']").GetAttribute("data-finding-id"));
        Assert.DoesNotContain(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"));
    }

    [Theory]
    [InlineData("batch-defer", FindingDecisionState.Deferred)]
    [InlineData("batch-reset", FindingDecisionState.Pending)]
    public async Task Unconfirmed_bulk_retains_selection_reports_unknown_and_does_not_replay(string command, FindingDecisionState state)
    {
        await using var c = Context(Details(Finding("a")));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch")).SetResult(new(500, null));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id)); page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find(".finding-row input").Change(true); page.Find($"[data-command='{command}']").Click();
        page.WaitForAssertion(() => Assert.Contains("Unknown", page.Markup)); Assert.Contains("1 selected", page.Markup);
        var call = Assert.Single(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions/batch"));
        Assert.Equal(state, Assert.Single(Assert.IsType<BatchFindingDecisionRequest>(call.Arguments[3]).Selections).State);
        Assert.True(page.Find($"[data-command='{command}']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(200, FindingDecisionStatus.Saved, 4, true)]
    [InlineData(200, FindingDecisionStatus.Saved, 3, false)]
    [InlineData(200, FindingDecisionStatus.Conflict, 4, false)]
    [InlineData(409, FindingDecisionStatus.Conflict, 4, false)]
    [InlineData(500, FindingDecisionStatus.Saved, 4, false)]
    public async Task Only_confirmed_individual_save_advances_and_never_posts(int status, FindingDecisionStatus response, long version, bool advances)
    {
        await using var c = Context(Details(Finding("a", FindingDecisionState.Pending), Finding("b", FindingDecisionState.Pending)));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions"))
            .SetResult(new(status, JsonSerializer.SerializeToElement(new FindingDecisionResponse(response, version, null))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        page.Find(".finding-row button").Click();
        page.Find("[data-command='approve']").Click();
        page.WaitForAssertion(() => Assert.Contains(advances ? "Decision saved" : status == 409 ? "decision changed" : "not confirmed", page.Markup));
        Assert.Equal(advances ? "b" : "a", page.Find("[aria-current='true']").GetAttribute("data-finding-id"));
        Assert.DoesNotContain(http.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"));
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Saved_decision_respects_stay_and_requires_refresh_before_advance(bool stay, bool failedRefresh)
    {
        await using var c = new BunitContext(); c.Services.AddScoped<DashboardBrowserClient>(); c.Services.AddSingleton(TimeProvider.System); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = c.JSInterop.SetupModule("./dashboard-http.js");
        var reads = http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/details"));
        reads.SetResult(new(200, JsonSerializer.SerializeToElement(Details(Finding("a", FindingDecisionState.Pending), Finding("b", FindingDecisionState.Pending)))));
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new FindingDecisionResponse(FindingDecisionStatus.Saved, 4, null))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        if (stay)
        {
            page.Find(".command-help").Click(); page.Find("[aria-label='Advance after saving']").Change(false); page.Find("[data-dialog-cancel]").Click();
        }
        reads.SetResult(failedRefresh ? new(500, null) : new(200, JsonSerializer.SerializeToElement(Details(Finding("a"), Finding("b")))));
        page.Find("[data-command='approve']").Click();
        page.WaitForAssertion(() => Assert.Contains("Decision saved", page.Markup));
        Assert.Equal("a", page.Find("[aria-current='true']").GetAttribute("data-finding-id"));
        if (failedRefresh) Assert.Contains("refresh failed", page.Markup);
        Assert.Equal(!stay && !failedRefresh, page.Markup.Contains("Review complete"));
        Assert.Equal("all", page.Find("select[aria-label='Decision filter']").GetAttribute("value"));
    }
    [Fact]
    public async Task Individual_drafts_warn_on_navigation_and_queue_survives_section_changes()
    {
        await using var c = Context(Details(Finding("a"), Finding("b")));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        page.Find("textarea").Input("retained individual reason");
        var navigation = page.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>();
        Assert.True(navigation.Instance.ConfirmExternalNavigation);
        var queue = page.FindComponent<ReviewForge.Service.Components.Dashboard.FindingQueue>().Instance;
        Assert.DoesNotContain("Latest observed head:", page.Markup);
        page.FindAll(".run-sections button")[1].Click();
        Assert.Same(queue, page.FindComponent<ReviewForge.Service.Components.Dashboard.FindingQueue>().Instance);
        page.FindAll(".run-sections button")[0].Click();
        Assert.Equal("retained individual reason", page.Find("textarea").GetAttribute("value"));
        c.JSInterop.Setup<bool>("confirm", _ => true).SetResult(false);
        var nav = c.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var previous = nav.Uri;
        await page.InvokeAsync(() => nav.NavigateTo("/dashboard"));
        Assert.Equal(previous, nav.Uri);
    }

    [Fact]
    public async Task Publication_preview_names_locations_versions_and_exclusions_and_retains_hidden_general_selection()
    {
        var approved = Finding("approved");
        approved = approved with { AcceptedFinding = approved.AcceptedFinding! with { Title = "<script>accepted</script>" } };
        await using var c = Context(Details(approved, Finding("pending", FindingDecisionState.Pending), Finding("unknown") with { PublicationUnknown = true }));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new PublishSelectedDraftResult(true, null, [new("approved", PublishFindingStatus.Posted)]))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".finding-row").Count));
        page.FindAll(".queue-selection button")[0].Click();
        page.Find("input[type=search]").Input("no match");
        page.Find("button.post-selected").Click();
        var preview = page.Find("dialog[aria-label='Confirm publication']");
        Assert.Contains("<script>accepted</script>", preview.TextContent);
        Assert.Empty(preview.QuerySelectorAll("script"));
        Assert.Contains("file.cs:2", preview.TextContent);
        Assert.DoesNotContain("file.cs:1", preview.TextContent);
        Assert.Contains("approval version 3", preview.TextContent);
        Assert.Contains("Not approved", preview.TextContent);
        Assert.Contains("Unknown publication outcome", preview.TextContent);
        page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("dialog")));
        Assert.Contains("3 selected · 3 hidden", page.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_mutation_result_cannot_write_into_another_run(bool publication)
    {
        await using var c = Context(Details(Finding()));
        var http = c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!;
        var response = http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/{(publication ? "publish" : "decisions")}"));
        var next = Guid.NewGuid();
        http.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{next:D}/details"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Details(Finding("new-run")))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        if (publication)
        { page.Find(".finding-row input").Change(true); page.Find("button.post-selected").Click(); page.Find("button.confirm-publication").Click(); }
        else page.Find(".finding-decision-editor button").Click();
        page.Render(p => p.Add(x => x.RunId, next));
        page.WaitForAssertion(() => Assert.Contains("new-run", page.Find("article.finding-card").TextContent));
        response.SetResult(new(200, JsonSerializer.SerializeToElement(publication ? (object)new PublishSelectedDraftResult(true, null, [new("finding", PublishFindingStatus.Unknown)])
            : new FindingDecisionResponse(FindingDecisionStatus.Saved, 4, null))));
        Assert.DoesNotContain("Publication outcomes", page.Markup);
        Assert.DoesNotContain("Decision saved", page.Markup);
        Assert.Contains("0 selected · 0 hidden", page.Markup);
        Assert.Contains(http.Invocations, i => i.Identifier == "abort");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Unconfirmed_publication_preserves_work_and_requires_explicit_reconciliation_without_replay(int status)
    {
        await using var c = Context(Details(Finding()));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/publish"))
            .SetResult(new(status, null));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row")));
        page.Find("textarea").Input("keep my reason");
        page.Find(".finding-row input").Change(true);
        page.Find("button.post-selected").Click(); page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("dialog")));
        Assert.Contains("1 selected · 0 hidden", page.Markup);
        Assert.Equal("keep my reason", page.Find("textarea").GetAttribute("value"));
        Assert.Single(page.FindAll("button.reconcile-publication"));
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        if (status >= 429)
        {
            page.Find("header button").Click();
            page.WaitForAssertion(() => Assert.False(page.Find("header button").HasAttribute("disabled")));
            Assert.Contains("Unknown", page.Find("article.finding-card").TextContent);
        }
        Assert.Single(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }

    [Fact]
    public async Task Saving_one_finding_retains_other_drafts_hidden_selection_and_stable_editor()
    {
        await using var c = Context(Details(Finding("a"), Finding("b")));
        c.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], $"/reviews/{Id:D}/decisions"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new FindingDecisionResponse(FindingDecisionStatus.Saved, 4, null))));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        page.Find("textarea").Input("a unsaved");
        page.Find(".finding-row input").Change(true);
        page.FindAll(".finding-row button")[1].Click();
        page.Find("input[type=search]").Input("no match");
        var editor = page.FindComponent<ReviewForge.Service.Components.Dashboard.FindingDecisionEditor>().Instance;
        page.Find(".finding-decision-editor button").Click();
        page.WaitForAssertion(() => Assert.Contains("Decision saved", page.Markup));
        Assert.Same(editor, page.FindComponent<ReviewForge.Service.Components.Dashboard.FindingDecisionEditor>().Instance);
        Assert.Contains("1 selected · 1 hidden", page.Markup);
        page.Find("input[type=search]").Input(""); page.FindAll(".finding-row button")[0].Click();
        Assert.Equal("a unsaved", page.Find("textarea").GetAttribute("value"));
    }

    [Fact]
    public async Task Queue_mounts_only_focused_evidence_and_keeps_general_selection_across_filters()
    {
        await using var c = Context(Details(Finding("a", FindingDecisionState.Pending), Finding("b", FindingDecisionState.Deferred)));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        Assert.Single(page.FindAll("article.finding-card"));
        page.FindAll(".finding-row input")[0].Change(true);
        page.Find("select[aria-label='Decision filter']").Change("Deferred");
        Assert.Contains("1 selected · 1 hidden", page.Markup);
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        page.Find(".finding-row button").Click();
        Assert.Contains("b", page.Find("article.finding-card").TextContent);
        page.Find(".return-to-queue").Click();
        Assert.Contains(c.JSInterop.Invocations, i => i.Identifier == "returnToRow" && Equals(i.Arguments[1], "b"));
        await page.InvokeAsync(() => page.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Inspector_and_filtered_return_handoff_focus_after_the_destination_render()
    {
        await using var c = Context(Details(Finding("a"), Finding("b", eligible: false)));
        var module = c.JSInterop.SetupModule("./dashboard-queue.js");
        module.SetupVoid("attach", _ => true).SetVoidResult();
        module.SetupVoid("enterInspector", _ => true).SetVoidResult();
        module.SetupVoid("returnToRow", _ => true).SetVoidResult();
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".finding-row").Count));
        Assert.Contains("Technically eligible", page.FindAll(".finding-row")[0].TextContent);
        Assert.Contains("Not technically eligible", page.FindAll(".finding-row")[1].TextContent);
        Assert.DoesNotContain(module.Invocations, i => i.Identifier == "enterInspector");
        page.Find(".finding-row button").Click();
        page.WaitForAssertion(() => Assert.Single(module.Invocations["enterInspector"]));
        Assert.Single(page.FindAll(".finding-workspace.inspecting"));
        Assert.Equal("-1", page.Find("[data-inspector-focus]").GetAttribute("tabindex"));
        page.Find("input[type=search]").Input("no matching finding");
        Assert.Empty(page.FindAll(".finding-row"));
        page.Find(".return-to-queue").Click();
        page.WaitForAssertion(() => Assert.Single(module.Invocations["returnToRow"]));
        Assert.Empty(page.FindAll(".finding-workspace.inspecting"));
        Assert.Single(page.FindAll("[data-queue-search]"));
    }

    [Fact]
    public async Task Select_all_matching_is_general_but_publication_snapshots_only_eligible_approved()
    {
        await using var c = Context(Details(Finding("approved"), Finding("pending", FindingDecisionState.Pending), Finding("unknown") with { PublicationUnknown = true }));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".finding-row").Count));
        page.FindAll(".queue-selection button")[0].Click();
        Assert.Contains("3 selected · 0 hidden", page.Markup);
        Assert.Contains("1 eligible approved selected · 2 excluded", page.Markup);
        page.Find("button.post-selected").Click();
        var confirmation = page.FindComponent<ReviewForge.Service.Components.Dashboard.RunPublicationConfirmation>();
        Assert.Equal("approved", Assert.Single(confirmation.Instance.Request.Selections!).FindingId);
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }

    [Fact]
    public async Task Hundreds_of_rows_keep_one_editor_and_identity_drafts_across_focus_and_refresh()
    {
        await using var c = Context(Details(Enumerable.Range(0, 300).Select(i => Finding($"f{i:D3}")).ToArray()));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Equal(300, page.FindAll(".finding-row").Count));
        Assert.Single(page.FindAll("textarea"));
        page.Find("textarea").Input("unsaved");
        page.FindAll(".finding-row button")[1].Click();
        page.Find("textarea").Input("other draft");
        page.FindAll(".finding-row button")[0].Click();
        Assert.Equal("unsaved", page.Find("textarea").GetAttribute("value"));
        page.Find("header button").Click();
        page.WaitForAssertion(() => Assert.False(page.Find("header button").HasAttribute("disabled")));
        Assert.Equal("unsaved", page.Find("textarea").GetAttribute("value"));
        page.Find("input[type=search]").Input("no match");
        Assert.Empty(page.FindAll(".finding-row"));
        Assert.Contains("hidden by the current filters", page.Markup);
    }

    [Fact]
    public async Task Deep_link_defaults_to_queue_escapes_payload_and_filters_rejected_and_posted()
    {
        await using var c = Context(Details(Finding(), Finding("rejected", FindingDecisionState.Rejected, eligible: false), Finding("posted", published: true)));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("article.finding-card")));
        Assert.Empty(page.FindAll("script"));
        Assert.Contains("&lt;script&gt;", page.Markup);
        Assert.Contains("validation reason", page.Markup);
        page.Find("select[aria-label='Pipeline filter']").Change("Pipeline-rejected");
        page.Find(".finding-row button").Click();
        Assert.Contains("human reason", page.Markup);
        Assert.Single(page.FindAll("article.finding-card"));
        page.Find("select[aria-label='Pipeline filter']").Change("all");
        page.Find("select[aria-label='Publication filter']").Change("Posted");
        page.Find(".finding-row button").Click();
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
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".finding-row input")));
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
        page.Find("input[type=checkbox]").Change(true);
        page.Find("button.post-selected").Click();
        page.Find("button.confirm-publication").Click();
        page.WaitForAssertion(() => Assert.Contains("Publication: Unknown", page.Find("article.finding-card").TextContent));
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
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
        Assert.False(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        Assert.Single(page.FindAll("button.reconcile-publication"));
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
    }

    [Fact]
    public async Task Known_changed_head_blocks_post_but_unknown_legacy_remains_explicit()
    {
        await using var c = Context(Details(Finding()) with { Source = new(null, null, "javascript:alert(1)", "changed", null, false, null) });
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Contains("new draft", page.Markup));
        Assert.False(page.Find("input[type=checkbox]").HasAttribute("disabled"));
        page.Find("input[type=checkbox]").Change(true);
        Assert.True(page.Find("button.post-selected").HasAttribute("disabled"));
        Assert.Contains("Reviewed head changed", page.Find(".publication-exclusions").TextContent);
        Assert.DoesNotContain("javascript:", page.Markup);
        await using var legacy = Context(Details() with { ReviewedHeadSha = null, ObservationsAvailable = false, Source = null });
        var old = legacy.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        old.WaitForAssertion(() => Assert.Contains("unavailable", old.Markup));
    }
    [Fact]
    public async Task General_selection_includes_all_states_while_evidence_stays_focused()
    {
        var rows = new[] { Finding(), Finding("pending", FindingDecisionState.Pending), Finding("deferred", FindingDecisionState.Deferred),
            Finding("rejected", FindingDecisionState.Rejected), Finding("ineligible", eligible: false), Finding("posted", published: true), Finding("unknown") with { Published = null } };
        await using var c = Context(Details(rows));
        var page = c.Render<RunDetail>(p => p.Add(x => x.RunId, Id));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("select[aria-label='Decision filter']")));
        var checks = page.FindAll("input[type=checkbox]");
        Assert.Equal(7, checks.Count);
        Assert.False(checks[0].HasAttribute("disabled"));
        Assert.All(checks, box => Assert.False(box.HasAttribute("disabled")));
        page.Find("select[aria-label='Decision filter']").Change("Rejected");
        Assert.Single(page.FindAll(".finding-row"));
        Assert.Single(page.FindAll("article.finding-card"));
        page.Find("select[aria-label='Decision filter']").Change("all");
        Assert.Equal(7, page.FindAll(".finding-row").Count);
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
        page.WaitForAssertion(() => Assert.Single(page.FindAll("select[aria-label='Publication filter']")));
        page.Find("select[aria-label='Publication filter']").Change("Posted");
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
