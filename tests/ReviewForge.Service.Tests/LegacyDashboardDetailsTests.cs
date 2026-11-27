using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Radzen;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

/// <summary>Standalone secondary-view regressions retained when the landing page became a worklist.</summary>
public sealed class LegacyDashboardDetailsTests
{
    private static DashboardRow Row() => new(new(new(new("org", "Project", "repo", 1), new FakePullRequestSource().Pr,
        "main", "author", "Ada"), "Repository", "https://example.com/pr"), false, true);
    private static DashboardResponse Data(params DashboardRow[] rows) => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], rows);
    private static BunitContext Context()
    {
        var c = new BunitContext();
        c.Services.AddRadzenComponents();
        c.Services.AddScoped<DashboardBrowserClient>();
        c.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        return c;
    }

    [Fact]
    public async Task Relative_times_update_for_every_run_presentation_and_live_metadata()
    {
        await using var c = Context();
        var clock = (FakeTimeProvider)c.Services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var attempt = new DashboardRun(Guid.NewGuid(), "head-sha", RunKind.Review, RunState.Running,
            StartedAt: now.AddMinutes(-5)) { UpdatedAt = now.AddMinutes(-2) };
        var success = new DashboardRun(Guid.NewGuid(), "head-sha", RunKind.Review, RunState.Completed, CompletedAt: now.AddMinutes(-10));
        var blocker = new DashboardRun(Guid.NewGuid(), "old-head", RunKind.Resolve, RunState.Running) { UpdatedAt = now.AddMinutes(-3) };
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = success, LatestAttempt = attempt, LatestPrAttempt = attempt, ActiveBlocker = blocker };
        var state = new DashboardRerunState();
        state.Reconcile(Data(row));
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row).Add(x => x.Reruns, state));
        Assert.Contains("2m ago", dialog.FindComponents<RunPresentation>().Single(p => p.Instance.Heading == "Latest PR attempt").Markup);
        await dialog.InvokeAsync(() => { clock.Advance(TimeSpan.FromMinutes(5)); state.Reconcile(Data(row)); });
        dialog.WaitForAssertion(() =>
        {
            var presentations = dialog.FindComponents<RunPresentation>();
            Assert.Equal(4, presentations.Count);
            Assert.All(presentations, p => Assert.Equal(clock.GetUtcNow(), p.Instance.Now));
            Assert.Contains("7m ago", presentations.Single(p => p.Instance.Heading == "Latest PR attempt").Markup);
        });
        var updated = attempt with { UpdatedAt = clock.GetUtcNow() };
        await dialog.InvokeAsync(() => state.Reconcile(Data(row with { LatestAttempt = updated, LatestPrAttempt = updated })));
        dialog.WaitForAssertion(() => Assert.Contains("Just now", dialog.FindComponents<RunPresentation>().Single(p => p.Instance.Heading == "Latest PR attempt").Markup));
        await dialog.InvokeAsync(() => state.Reconcile(Data()));
        dialog.WaitForAssertion(() => Assert.Contains("no longer in the retrieved inventory", dialog.Markup));
        Assert.Contains("Reviewed", dialog.Markup);
        Assert.True(dialog.Find("button[aria-label='Rerun review']").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Skipped)]
    public async Task Successful_coverage_and_metadata_survive_unsuccessful_attempt(RunState state)
    {
        await using var c = Context();
        var success = new DashboardRun(Guid.NewGuid(), "head", RunKind.Review, RunState.Completed, ReviewFindings: 7);
        var latest = new DashboardRun(Guid.NewGuid(), "head", RunKind.Review, state);
        var row = Row() with { CurrentHeadReviewed = true, LastSuccessfulReview = success, LatestAttempt = latest, LatestPrAttempt = latest };
        var details = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains(success.RunId.ToString(), details.Markup);
        Assert.Contains("Review findings: 7", details.Markup);
        Assert.Contains(state.ToString(), details.Markup);
    }

    [Theory]
    [InlineData(null, RunState.Failed, "Head not captured")]
    [InlineData("different-head", RunState.Completed, "Different head")]
    public async Task Latest_pr_attempt_does_not_fabricate_current_head_history(string? head, RunState state, string attribution)
    {
        await using var c = Context();
        var attempt = new DashboardRun(Guid.NewGuid(), head, RunKind.Review, state, CurrentStage: "FetchPrContext", ProgressPercent: 0)
            { LoadedStages = ["FetchPrContext", "ReviewGate"] };
        var row = Row() with { LatestPrAttempt = attempt };
        var dialog = c.Render<ReviewDetailsDialog>(p => p.Add(x => x.Row, row));
        Assert.Contains(attribution, dialog.Markup);
        Assert.Contains(attempt.RunId.ToString(), dialog.Markup);
        Assert.Contains("Latest current-head attempt", dialog.Markup);
        Assert.Contains("No current-head review attempt", dialog.Markup);
        Assert.Equal(state, dialog.FindComponents<RunPresentation>().Single(r => r.Instance.Heading == "Latest PR attempt").Instance.Run!.State);
        Assert.Null(row.LastSuccessfulReview);
        Assert.Null(row.LatestAttempt);
    }
}
