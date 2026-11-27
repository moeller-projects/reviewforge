using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using ReviewForge.Service.Components.Pages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public class DashboardRerunTests
{
    private static DashboardRow Row() => new(new(new(new("org", "Project", "repo", 1),
        new FakePullRequestSource().Pr, "main", "author", "Ada"), "Repository", "https://example.com/pr"), false, true);
    private static DashboardResponse Data(DashboardRow row) => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], [row]);
    private static BunitContext Context()
    {
        var c = new BunitContext();
        c.Services.AddScoped<DashboardConfirmation>();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        return c;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standalone_actions_share_pending_confirmation_cancel_then_submit_and_refresh(bool fromDetails)
    {
        await using var c = Context();
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[1], "GET"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()))));
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var post = module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i =>
        {
            if (!Equals(i.Arguments[1], "POST")) return false;
            submitted.TrySetResult();
            return true;
        });
        var row = Row();
        var state = new DashboardRerunState();
        async Task Refresh()
        {
            var result = await c.Services.GetRequiredService<DashboardBrowserClient>().GetDashboardAsync();
            if (result.Value is { } value) state.Reconcile(value);
        }
        await Refresh();
        var dialogs = c.Render<ConfirmationHost>();
        var page = c.Render<RerunReviewButton>(p => p.Add(x => x.Row, row).Add(x => x.State, state).Add(x => x.OnAccepted, Refresh));
        var details = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row).Add(x => x.Reruns, state).Add(x => x.OnAccepted, Refresh));
        details.Find("button[aria-label='Rerun review']").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Drafts", dialogs.Markup));
        Assert.Equal(details.Find("span[tabindex='0']").Id, dialogs.FindComponent<WorkbenchDialog>().Instance.ReturnFocusId);
        Assert.True(page.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
        Assert.Single(dialogs.FindAll("dialog"));
        dialogs.Find("[data-dialog-cancel]").Click();
        page.WaitForAssertion(() => Assert.False(page.Find("button[aria-label='Rerun review']").HasAttribute("disabled")));
        Assert.DoesNotContain(module.Invocations["send"], i => Equals(i.Arguments[1], "POST"));
        if (fromDetails) details.Find("button[aria-label='Rerun review']").Click();
        else page.Find("button[aria-label='Rerun review']").Click();
        dialogs.Find("button.primary-action").Click();
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Single(module.Invocations["send"], i => Equals(i.Arguments[1], "POST"));
        var request = Assert.IsType<SubmitReviewRequest>(module.Invocations["send"].Last().Arguments[3]);
        Assert.Equal(new("org", "Project", "repo", 1, true), request);
        var runId = Guid.NewGuid();
        post.SetResult(new(202, JsonSerializer.SerializeToElement(new SubmitReviewResponse(runId, "/reviews/" + runId))));
        page.WaitForAssertion(() => Assert.Contains(runId.ToString(), page.Markup));
        Assert.Contains("Queued", page.Markup);
        page.WaitForAssertion(() => Assert.Equal(2, module.Invocations["send"].Count(i => Equals(i.Arguments[1], "GET"))));
        Assert.True(page.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(409, "already in flight")]
    [InlineData(400, "invalid")]
    [InlineData(503, "queue is full")]
    [InlineData(429, "Too many")]
    [InlineData(401, "Session expired")]
    [InlineData(403, "Access denied")]
    [InlineData(0, "connection")]
    public async Task Outcomes_are_safe_distinct_clear_pending_and_preserve_row(int status, string expected)
    {
        await using var c = Context();
        var module = c.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[1], "GET"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(Row()))));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[1], "POST"))
            .SetResult(new(status, JsonSerializer.SerializeToElement(new { error = "<secret>" })));
        var confirmation = c.Services.GetRequiredService<DashboardConfirmation>();
        var row = Row();
        var state = new DashboardRerunState();
        state.Reconcile(Data(row));
        var page = c.Render<RerunReviewButton>(p => p.Add(x => x.Row, row).Add(x => x.State, state));
        page.Find("button[aria-label='Rerun review']").Click();
        await page.InvokeAsync(() => confirmation.Complete(true));
        page.WaitForAssertion(() => Assert.Contains(expected, confirmation.Message, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("secret", confirmation.Message);
        Assert.Equal(row, state.Current(row.PullRequest.Candidate.Key));
        Assert.Equal(status is 401 or 403, page.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
        if (status is 401 or 403)
            Assert.EndsWith("/dashboard/login", c.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    [Fact]
    public async Task Accepted_feedback_is_immediate_even_while_refresh_is_pending_and_context_is_accessible()
    {
        await using var c = Context();
        var row = Row();
        var state = new DashboardRerunState();
        var runId = Guid.NewGuid();
        var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(202, JsonSerializer.SerializeToElement(new SubmitReviewResponse(runId, "/reviews/" + runId))));
        var button = c.Render<RerunReviewButton>(p => p.Add(x => x.Row, row).Add(x => x.State, state)
            .Add(x => x.OnAccepted, () => refresh.Task));
        Assert.Contains("safeguards", button.Find("span[tabindex='0']").GetAttribute("aria-label"));
        button.Find("button").Click();
        await button.InvokeAsync(() => c.Services.GetRequiredService<DashboardConfirmation>().Complete(true));
        button.WaitForAssertion(() => Assert.Contains(runId.ToString(), button.Markup));
        Assert.False(refresh.Task.IsCompleted);
        Assert.True(button.Find("button").HasAttribute("disabled"));
        refresh.SetResult();
        button.WaitForAssertion(() => Assert.Contains("Waiting for operational status", button.Markup));
    }

    [Fact]
    public async Task Disposal_during_confirmation_does_not_submit_and_pending_is_released()
    {
        await using var c = Context();
        var state = new DashboardRerunState();
        var row = Row();
        var button = c.Render<RerunReviewButton>(p => p.Add(x => x.Row, row).Add(x => x.State, state));
        button.Find("button").Click();
        Assert.False(state.CanBegin(row));
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Changed += () => { if (state.CanBegin(row)) released.TrySetResult(); };
        await c.DisposeComponentsAsync();
        c.Services.GetRequiredService<DashboardConfirmation>().Complete(true);
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.True(state.CanBegin(row));
        Assert.DoesNotContain(c.JSInterop.Invocations, i => i.Identifier == "send");
    }

    [Fact]
    public void Refreshed_rows_replace_stale_availability_and_accepted_overlay_reconciles_without_head_assumptions()
    {
        var row = Row();
        var state = new DashboardRerunState();
        var key = row.PullRequest.Candidate.Key;
        Assert.True(state.TryBegin(row));
        Assert.False(state.TryBegin(row));
        state.End(key);
        var id = Guid.NewGuid();
        state.Accepted(key, id);
        state.Reconcile(Data(row));
        Assert.Equal(id, state.AcceptedRun(key));
        state.Reconcile(Data(row with { LatestAttempt = new(id, "head", RunKind.Review, RunState.Skipped) }));
        Assert.Null(state.AcceptedRun(key));
        Assert.True(state.CanBegin(row));
        state.Reconcile(Data(row with { CanRerun = null }));
        Assert.False(state.TryBegin(row));
        Assert.Contains("unknown", state.Context(row));
        state.Reconcile(Data(row with { ActiveBlocker = new(Guid.NewGuid(), null, RunKind.Resolve, RunState.Running) }));
        Assert.False(state.TryBegin(row));
        Assert.Contains("blocked", state.Context(row));
    }

    [Fact]
    public void Removed_inventory_prs_clear_all_page_owned_state_and_disable_stale_details()
    {
        var state = new DashboardRerunState();
        var row = Row();
        var key = row.PullRequest.Candidate.Key;
        state.Reconcile(Data(row));
        Assert.True(state.TryBegin(row));
        state.Accepted(key, Guid.NewGuid());
        Assert.True(state.AwaitingAccepted);
        state.Reconcile(Data(row) with { Rows = [] });
        Assert.Null(state.Current(key));
        Assert.Null(state.AcceptedRun(key));
        Assert.False(state.AwaitingAccepted);
        Assert.False(state.CanBegin(row));
        Assert.Contains("no longer", state.Context(row));
        state.Reconcile(Data(row));
        Assert.True(state.CanBegin(row));
        state.ExpireAuthentication();
        Assert.False(state.CanBegin(row));
        Assert.Contains("expired", state.Context(row));
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Skipped)]
    public void Accepted_terminal_run_clears_even_when_not_a_current_head_attempt(RunState terminal)
    {
        var row = Row();
        var state = new DashboardRerunState();
        var key = row.PullRequest.Candidate.Key;
        var id = Guid.NewGuid();
        state.Accepted(key, id);
        state.Reconcile(Data(row) with { RunStates = new Dictionary<Guid, RunState?> { [id] = null } });
        Assert.Equal(id, state.AcceptedRun(key));
        state.Reconcile(Data(row) with { RunStates = new Dictionary<Guid, RunState?> { [id] = terminal } });
        Assert.Null(state.AcceptedRun(key));
        Assert.True(state.CanBegin(row));
    }

    [Theory]
    [InlineData(RunKind.Resolve, null)]
    [InlineData(RunKind.ReviewDraft, "old")]
    [InlineData(RunKind.Review, "old")]
    public async Task Any_kind_old_or_unknown_head_blocker_disables_both_actions_even_with_stale_canrerun(RunKind kind, string? head)
    {
        await using var c = Context();
        var row = Row() with { ActiveBlocker = new(Guid.NewGuid(), head, kind, RunState.Queued) };
        c.JSInterop.SetupModule("./dashboard-http.js").Setup<DashboardBrowserClient.BrowserHttpResponse>("send", _ => true)
            .SetResult(new(200, JsonSerializer.SerializeToElement(Data(row))));
        var state = new DashboardRerunState();
        state.Reconcile(Data(row));
        var button = c.Render<RerunReviewButton>(p => p.Add(x => x.Row, row).Add(x => x.State, state));
        var details = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row).Add(x => x.Reruns, state));
        Assert.True(button.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
        Assert.True(details.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
    }
}
