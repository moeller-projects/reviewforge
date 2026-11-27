using Bunit;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunWorklistComponentTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    [Fact]
    public async Task Attention_opens_relevant_revision_and_leaves_old_history_collapsed()
    {
        await using var context = new BunitContext();
        var recent = Item("new", Now, pending: 1);
        var old = Item("old", Now.AddHours(-1));
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [old, recent]).Add(x => x.Now, Now));

        Assert.Equal("true", component.Find(".worklist-pr-toggle").GetAttribute("aria-expanded"));
        Assert.Equal(2, component.FindAll(".worklist-revision-toggle").Count);
        var visibleRun = Assert.Single(component.FindAll(".worklist-run-link"));
        Assert.Equal($"/dashboard/runs/{recent.Run.RunId}", visibleRun.GetAttribute("href"));
        Assert.Contains("Current head", component.Markup);
        Assert.Contains("1 pending", component.Markup);
        Assert.DoesNotContain("<progress", component.Markup);
    }

    [Fact]
    public async Task User_collapse_survives_refresh()
    {
        await using var context = new BunitContext();
        var item = Item("new", Now, pending: 1);
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [item]));
        component.Find(".worklist-pr-toggle").Click();

        component.Render(p => p.Add(x => x.Items, [item with { PendingDecisions = 2 }]));

        Assert.Equal("false", component.Find(".worklist-pr-toggle").GetAttribute("aria-expanded"));
        Assert.Empty(component.FindAll(".worklist-run-link"));
        component.Find(".worklist-pr-toggle").Click();
        Assert.Contains("2 pending", component.Markup);
    }

    [Fact]
    public async Task All_view_can_expand_closed_pr_history_and_unknown_revision_without_metadata()
    {
        await using var context = new BunitContext();
        var item = Item(null, Now) with { Title = null, RepositoryName = null };
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [item]).Add(x => x.AttentionOnly, false));

        Assert.Contains("PR #42", component.Markup);
        Assert.Contains("org / project / repo", component.Markup);
        component.Find(".worklist-pr-toggle").Click();
        Assert.Contains("Unknown revision", component.Markup);
        component.Find(".worklist-revision-toggle").Click();
        Assert.Single(component.FindAll(".worklist-run-link"));
        Assert.Contains("Findings unavailable", component.Markup);
        Assert.DoesNotContain("Current head", component.Markup);
    }

    [Fact]
    public async Task Newly_attention_worthy_run_expands_unless_user_made_a_manual_choice()
    {
        await using var context = new BunitContext();
        var item = Item("new", Now);
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [item]).Add(x => x.AttentionOnly, false));
        Assert.Equal("false", component.Find(".worklist-pr-toggle").GetAttribute("aria-expanded"));
        component.Render(p => p.Add(x => x.Items, [item with { PendingDecisions = 1 }]));
        Assert.Single(component.FindAll(".worklist-run-link"));
        component.Find(".worklist-pr-toggle").Click();
        component.Render(p => p.Add(x => x.Items, [item with { PendingDecisions = 2 }]));
        Assert.Empty(component.FindAll(".worklist-run-link"));
    }

    [Fact]
    public async Task Manual_expansion_survives_search_removal_and_attention_filters()
    {
        await using var context = new BunitContext();
        var item = Item("new", Now, pending: 1);
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [item]));
        component.Find(".worklist-revision-toggle").Click();
        component.Render(p => p.Add(x => x.Items, []));
        component.Render(p => p.Add(x => x.Items, [item]).Add(x => x.AttentionOnly, false));
        Assert.Equal("false", component.Find(".worklist-revision-toggle").GetAttribute("aria-expanded"));
        Assert.Empty(component.FindAll(".worklist-run-link"));
    }

    [Fact]
    public async Task Active_stage_is_a_stage_count_not_time_progress_and_unknown_counts_stay_unknown()
    {
        await using var context = new BunitContext();
        var item = Item(null, Now) with { Run = new(Guid.NewGuid(), null, RunKind.ReviewDraft, RunState.Running,
            CurrentStage: "ReviewGate", ProgressPercent: 50) { LoadedStages = ["FetchPrContext", "ReviewGate", "PersistRun"] } };
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [item]));
        Assert.Contains("stage 2/3", component.Markup);
        Assert.Contains("Findings unavailable", component.Markup);
        Assert.DoesNotContain("50%", component.Markup);
        Assert.DoesNotContain("0 accepted", component.Markup);
        component.Render(p => p.Add(x => x.Items, [item with { Run = item.Run with { CurrentStage = null, LoadedStages = [] } }]));
        Assert.Contains("Waiting for worker", component.Markup);
        Assert.DoesNotContain("stage 2/3", component.Markup);
    }

    [Fact]
    public async Task Empty_attention_view_has_a_clear_message()
    {
        await using var context = new BunitContext();
        var component = context.Render<RunWorklist>(p => p.Add(x => x.Items, [Item("new", Now)]));
        Assert.Contains("No runs need attention", component.Markup);
    }

    private static RunWorklistItem Item(string? head, DateTimeOffset started, int pending = 0) => new(Key,
        new DashboardRun(Guid.NewGuid(), head, RunKind.ReviewDraft, RunState.Completed,
            StartedAt: started, CompletedAt: started.AddMinutes(1)),
        Title: "PR title", RepositoryName: "Repository", CurrentHeadSha: "new", PendingDecisions: pending, ApprovedUnpublished: 0);
}
