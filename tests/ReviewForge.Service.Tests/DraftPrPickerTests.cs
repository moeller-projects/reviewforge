using Bunit;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class DraftPrPickerTests
{
    [Fact]
    public async Task Picker_searches_inventory_and_selects_draft_pr_even_when_ordinary_rerun_is_ineligible()
    {
        await using var context = new BunitContext();
        PrKey? selected = null;
        var draft = Row(1, "One", draft: true) with { CanRerun = false };
        var component = context.Render<DraftPrPicker>(p => p.Add(x => x.Rows, [draft, Row(2, "Two")])
            .Add(x => x.VerificationEnabled, true).Add(x => x.OnSelected, key => selected = key));

        component.Find("input[aria-label='Search pull requests for draft review']").Input("One");
        var button = Assert.Single(component.FindAll("button.draft-pr-choice"));
        Assert.Contains("Draft PR", button.TextContent);
        Assert.False(button.HasAttribute("disabled"));
        button.Click();

        Assert.Equal(draft.PullRequest.Candidate.Key, selected);
        Assert.Contains("Nothing is posted automatically", component.Markup);
    }

    [Fact]
    public async Task Missing_verifier_and_active_claim_disable_submission_with_explanations()
    {
        await using var context = new BunitContext();
        var row = Row(1, "One");
        var component = context.Render<DraftPrPicker>(p => p.Add(x => x.Rows, [row]));
        Assert.True(component.Find("button.draft-pr-choice").HasAttribute("disabled"));
        Assert.Contains("Finding verification must be enabled", component.Markup);

        component.Render(p => p.Add(x => x.VerificationEnabled, true).Add(x => x.Rows,
            [row with { ActiveBlocker = new(Guid.NewGuid(), "head", RunKind.ReviewDraft, RunState.Running) }]));
        Assert.True(component.Find("button.draft-pr-choice").HasAttribute("disabled"));
        Assert.Contains("A run is already in flight", component.Markup);
    }

    [Fact]
    public async Task Empty_search_has_clear_feedback()
    {
        await using var context = new BunitContext();
        var component = context.Render<DraftPrPicker>(p => p.Add(x => x.Rows, [Row(1, "One")]).Add(x => x.VerificationEnabled, true));
        component.Find("input").Input("absent");
        Assert.Empty(component.FindAll("button.draft-pr-choice"));
        Assert.Contains("No matching pull requests", component.Markup);
    }

    private static DashboardRow Row(int id, string repository, bool draft = false)
    {
        var source = new FakePullRequestSource();
        return new(new(new(new("org", "Project", repository, id), source.Pr with { Id = id, IsDraft = draft },
            "main", "author", "Ada"), repository, "https://example.com/pr"), false, true);
    }
}
