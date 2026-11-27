using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Pages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class StartReviewPageTests
{
    private static DashboardResponse Inventory()
    {
        var source = new FakePullRequestSource();
        var row = new DashboardRow(new(new(new("org", "project", "repo", 42), source.Pr with { IsDraft = true },
            "main", "creator", "Author"), "Repository", "https://example.com/pr"), false, true);
        return new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], [row]);
    }

    private static BunitContext Context(bool capability = true, int inventoryStatus = 200, DashboardResponse? inventory = null)
    {
        var context = new BunitContext();
        context.Services.AddRadzenComponents();
        context.Services.AddScoped<DashboardBrowserClient>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./dashboard-http.js");
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], "/reviews/dashboard"))
            .SetResult(new(inventoryStatus, JsonSerializer.SerializeToElement(inventory ?? Inventory())));
        module.Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], "/reviews/drafts/capability"))
            .SetResult(new(200, JsonSerializer.SerializeToElement(new DraftCapabilityResponse(capability, capability ? null : "verification-disabled"))));
        return context;
    }

    [Fact]
    public async Task Primary_action_starts_read_only_draft_and_navigates_to_accepted_run()
    {
        await using var context = Context();
        var id = Guid.NewGuid();
        context.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], "/reviews/drafts"))
            .SetResult(new(202, JsonSerializer.SerializeToElement(new SubmitReviewResponse(id, $"/reviews/{id}"))));
        var page = context.Render<StartReview>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.draft-pr-choice")));

        page.Find("button.draft-pr-choice").Click();

        page.WaitForAssertion(() => Assert.EndsWith($"/dashboard/runs/{id}", context.Services.GetRequiredService<NavigationManager>().Uri));
        var submission = Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        var request = Assert.IsType<SubmitReviewRequest>(submission.Arguments[3]);
        Assert.False(request.ForceReview);
        Assert.Equal(42, request.PrId);
        Assert.DoesNotContain(context.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], "/reviews"));
    }

    [Fact]
    public async Task Disabled_verifier_or_expired_session_prevents_submission()
    {
        await using var disabled = Context(capability: false);
        var page = disabled.Render<StartReview>();
        page.WaitForAssertion(() => Assert.True(page.Find("button.draft-pr-choice").HasAttribute("disabled")));
        Assert.Contains("Finding verification must be enabled", page.Markup);

        await using var expired = Context(inventoryStatus: 401);
        var expiredPage = expired.Render<StartReview>();
        expiredPage.WaitForAssertion(() => Assert.Contains("Session expired", expiredPage.Markup));
        Assert.Empty(expiredPage.FindAll("button.draft-pr-choice"));
    }

    [Fact]
    public async Task Changing_advanced_selection_during_submission_navigates_to_the_run_actually_accepted()
    {
        var inventory = Inventory();
        var first = inventory.Rows[0];
        var second = first with { PullRequest = first.PullRequest with { Candidate = first.PullRequest.Candidate with
            { Key = first.PullRequest.Candidate.Key with { PrId = 43 } } } };
        await using var context = Context(inventory: inventory with { Rows = [first, second] });
        var post = context.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], "/reviews"));
        var page = context.Render<StartReview>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("select[aria-label='Select PR for automatic publication']")));
        page.Find("select").Change("0");
        page.Find("button[aria-label='Rerun review']").Click();
        await page.InvokeAsync(() => context.Services.GetRequiredService<DialogService>().Close(true));
        page.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[2], "/reviews")));
        page.Find("select").Change("1");
        var accepted = Guid.NewGuid();
        post.SetResult(new(202, JsonSerializer.SerializeToElement(new SubmitReviewResponse(accepted, $"/reviews/{accepted}"))));
        page.WaitForAssertion(() => Assert.EndsWith($"/dashboard/runs/{accepted}", context.Services.GetRequiredService<NavigationManager>().Uri));
    }

    [Fact]
    public async Task Ambiguous_submission_is_not_automatically_retried()
    {
        await using var context = Context();
        context.JSInterop.TryGetModuleJSInterop("import", ["./dashboard-http.js"])!
            .Setup<DashboardBrowserClient.BrowserHttpResponse>("send", i => Equals(i.Arguments[2], "/reviews/drafts"))
            .SetResult(new(0, null));
        var page = context.Render<StartReview>();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("button.draft-pr-choice")));
        page.Find("button.draft-pr-choice").Click();

        page.WaitForAssertion(() => Assert.Contains("may have reached the server", page.Markup));
        Assert.Single(context.JSInterop.Invocations, i => i.Identifier == "send" && Equals(i.Arguments[1], "POST"));
        Assert.Contains("Automatic publication", page.Markup);
    }
}
