using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingCommandTests
{
    private static RunFindingDto Row(string id) => new(id, new("RULE", id, "high", "bug", "claim", null, null, new("file", 1, 2), false), null, null, false, null, 0, false, null, false, true);
    [Fact]
    public async Task Commands_share_selection_focus_reason_and_guarded_save_without_publication()
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "a" }; var saves = new List<SetFindingDecisionRequest>();
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.Findings, new[] { Row("a"), Row("b"), Row("c") }).Add(x => x.HeadSha, "head").Add(x => x.OnSave, r => saves.Add(r)));
        Assert.Single(view.FindAll("[data-finding-id][tabindex='0']"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("next", true));
        Assert.Equal("b", state.FocusedId); Assert.Equal(2, state.Selected.Count);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("select", false)); Assert.DoesNotContain("b", state.Selected);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("inspect", false)); Assert.Single(view.FindAll(".inspecting"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("reject", false));
        Assert.Empty(saves); Assert.Equal(FindingDecisionState.Rejected, state.Drafts["b"].State);
        Assert.True(view.Find(".finding-decision-editor button").HasAttribute("disabled"));
        view.Find("textarea").Input("required reason"); view.Find(".finding-decision-editor button").Click();
        Assert.Equal("required reason", Assert.Single(saves).Reason);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("escape", false)); Assert.Empty(view.FindAll(".inspecting")); Assert.Equal("required reason", state.Drafts["b"].Reason);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("escape", false)); Assert.Empty(state.Selected);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("next", false));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("approve", false)); Assert.Equal(FindingDecisionState.Approved, saves.Last().State);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("defer", false)); Assert.Equal(FindingDecisionState.Deferred, saves.Last().State);
        view.Render(p => p.Add(x => x.Disabled, true));
        var count = saves.Count; await view.InvokeAsync(() => view.Instance.ExecuteCommand("approve", false)); Assert.Equal(count, saves.Count);
    }
    [Fact]
    public async Task Remembered_preferences_navigation_bounds_and_completion_have_control_alternatives()
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var commands = c.JSInterop.SetupModule("./dashboard-commands.js");
        commands.Setup<FindingQueue.CommandPreferences>("attach", _ => true).SetResult(new(true, false));
        var state = new FindingWorkspaceState { FocusedId = "a", Complete = true };
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.Findings, new[] { Row("a"), Row("b") }).Add(x => x.HeadSha, "head"));
        view.WaitForAssertion(() => Assert.False(state.Advance));
        Assert.Contains("Review complete", view.Markup);
        view.Find("input[type=search]").Input("a"); Assert.DoesNotContain("Review complete", view.Markup);
        view.Find("input[type=search]").Input("");
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("previous", false)); Assert.Equal("a", state.FocusedId); Assert.False(state.Complete);
        view.Find(".command-help").Click();
        Assert.True(view.Find("[aria-label='Single-letter shortcuts']").HasAttribute("checked"));
        view.FindAll(".command-list button")[0].Click(); Assert.Equal("b", state.FocusedId); Assert.Empty(view.FindAll("dialog"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("next", false)); Assert.Equal("b", state.FocusedId);
        view.Find("input[type=search]").Input("no matching findings");
        Assert.Empty(view.FindAll("[data-finding-id][tabindex='0']"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("approve", false)); Assert.Contains("Choose a finding", view.Markup);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("return", false));
    }
    [Fact]
    public async Task Keyboard_palette_preserves_invoking_focus_instead_of_forcing_help_entry()
    {
        await using var c = new BunitContext();
        c.JSInterop.Mode = JSRuntimeMode.Loose;
        var dialogs = c.JSInterop.SetupModule("./dashboard-dialog.js");
        dialogs.SetupVoid("open", _ => true).SetVoidResult();
        dialogs.SetupVoid("close", _ => true).SetVoidResult();
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, new FindingWorkspaceState { FocusedId = "a" })
            .Add(x => x.Findings, new[] { Row("a") }).Add(x => x.HeadSha, "head"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("commands"));
        view.WaitForAssertion(() => Assert.Single(dialogs.Invocations["open"]));
        Assert.Null(Assert.Single(dialogs.Invocations["open"]).Arguments[1]);
        view.Find("[data-dialog-cancel]").Click();
        view.WaitForAssertion(() => Assert.Single(dialogs.Invocations["close"]));
        Assert.Single(view.FindAll("[data-finding-id='a'][tabindex='0']"));
    }

    [Fact]
    public async Task Palette_preferences_and_active_context_are_safe_and_disposed_commands_retire()
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "a" };
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.Findings, new[] { Row("a") }).Add(x => x.HeadSha, "head"));
        view.Find(".command-help").Click(); Assert.Single(view.FindAll("dialog"));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("next", false)); Assert.Equal("a", state.FocusedId);
        Assert.False(view.Find("[aria-label='Single-letter shortcuts']").HasAttribute("checked"));
        view.Find("[aria-label='Single-letter shortcuts']").Change(true);
        view.Find("[aria-label='Advance after saving']").Change(false); Assert.False(state.Advance);
        view.Find("[data-dialog-cancel]").Click(); Assert.Empty(view.FindAll("dialog"));
        state.Drafts["a"] = new(0, FindingDecisionState.Pending, "unsaved") { Dirty = true };
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("approve", false)); Assert.Contains("unsaved", view.Markup);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("search", false));
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("unknown", false));
        await view.InvokeAsync(() => view.Instance.DisposeAsync().AsTask());
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("commands", false)); Assert.Empty(view.FindAll("dialog"));
    }
}
