using Bunit;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingDecisionEditorTests
{
    [Fact]
    public async Task Rejection_requires_reason_and_submits_pinned_revision_and_expected_version()
    {
        await using var context = new BunitContext();
        SetFindingDecisionRequest? saved = null;
        var finding = Finding() with { DecisionVersion = 4 };
        var component = context.Render<FindingDecisionEditor>(p => p.Add(x => x.Finding, finding)
            .Add(x => x.HeadSha, "reviewed-head").Add(x => x.OnSave, request => saved = request));

        component.Find("select").Change(nameof(FindingDecisionState.Rejected));
        Assert.True(component.Find("button").HasAttribute("disabled"));
        component.Find("textarea").Input("The caller already checks this case.");
        component.Find("button").Click();

        Assert.NotNull(saved);
        Assert.Equal("finding", saved.FindingId);
        Assert.Equal("reviewed-head", saved.HeadSha);
        Assert.Equal(4, saved.ExpectedVersion);
        Assert.Equal(FindingDecisionState.Rejected, saved.State);
        Assert.Equal("The caller already checks this case.", saved.Reason);
    }

    [Fact]
    public async Task Approval_is_only_a_saved_decision_not_publication()
    {
        await using var context = new BunitContext();
        SetFindingDecisionRequest? saved = null;
        var component = context.Render<FindingDecisionEditor>(p => p.Add(x => x.Finding, Finding())
            .Add(x => x.HeadSha, "head").Add(x => x.OnSave, request => saved = request));
        component.Find("select").Change(nameof(FindingDecisionState.Approved));
        component.Find("button").Click();

        Assert.Equal(FindingDecisionState.Approved, saved!.State);
        Assert.Contains("Approval does not post", component.Markup);
        Assert.Equal("Save decision", component.Find("button").TextContent.Trim());
    }

    [Fact]
    public async Task Non_editable_findings_disable_controls_and_new_authoritative_version_resets_form()
    {
        await using var context = new BunitContext();
        var finding = Finding() with { CanDecide = false };
        var component = context.Render<FindingDecisionEditor>(p => p.Add(x => x.Finding, finding).Add(x => x.HeadSha, "head"));
        Assert.True(component.Find("select").HasAttribute("disabled"));
        Assert.True(component.Find("button").HasAttribute("disabled"));

        var decision = new FindingDecisionDto(Guid.NewGuid(), "finding", "head", FindingDecisionState.Deferred,
            "Awaiting context", "dashboard-session", DateTimeOffset.UtcNow, 1);
        component.Render(p => p.Add(x => x.Finding, finding with { CanDecide = true, DecisionVersion = 1, Decision = decision }));
        Assert.Equal(nameof(FindingDecisionState.Deferred), component.Find("select").GetAttribute("value"));
        Assert.Equal("Awaiting context", component.Find("textarea").GetAttribute("value"));
    }

    [Fact]
    public async Task Dirty_draft_survives_authoritative_version_change_until_explicitly_acknowledged()
    {
        await using var context = new BunitContext();
        SetFindingDecisionRequest? saved = null;
        var component = context.Render<FindingDecisionEditor>(p => p.Add(x => x.Finding, Finding())
            .Add(x => x.HeadSha, "head").Add(x => x.OnSave, request => saved = request));
        component.Find("textarea").Input("my unsaved reason");
        component.Render(p => p.Add(x => x.Finding, Finding() with { DecisionVersion = 2 }));
        Assert.Equal("my unsaved reason", component.Find("textarea").GetAttribute("value"));
        Assert.Contains("Authoritative decision changed", component.Markup);
        Assert.True(component.FindAll("button").Last().HasAttribute("disabled"));
        component.FindAll("button").First().Click();
        component.Find("button").Click();
        Assert.Equal(2, saved!.ExpectedVersion);
        Assert.Equal("my unsaved reason", saved.Reason);
    }

    private static RunFindingDto Finding() => new("finding", null, null, null, false,
        null, 0, false, null, true, true);
}
