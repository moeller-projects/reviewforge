using Bunit;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using ReviewForge.Core.Domain;
using ReviewForge.Service.Components.Dashboard;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingBoardTests
{
    private static RunFindingDto Row(string id, FindingDecisionState? decision = null, bool canDecide = true) =>
        new(id, new("RULE", id, "high", "bug", "claim", null, null, new("file.cs", 1, 2), false), null, null, false,
            decision is null ? null : new FindingDecisionDto(Guid.NewGuid(), id, "head", decision.Value, null, "dashboard-session", DateTimeOffset.UtcNow, 3), 3, false, null, false, canDecide);

    [Fact]
    public void Queue_is_default_board_projects_authoritative_decisions_and_switch_keeps_context()
    {
        using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "a", Search = "" };
        state.Selected.UnionWith(["a", "b"]);
        state.Drafts["a"] = new(3, FindingDecisionState.Rejected, "keep my reason") { Dirty = true };
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.HeadSha, "head")
            .Add(x => x.Findings, new[] { Row("a"), Row("b", FindingDecisionState.Approved), Row("c", FindingDecisionState.Deferred), Row("d", FindingDecisionState.Rejected) }));
        Assert.Empty(view.FindAll(".finding-board"));
        view.Find("[data-view='board']").Click();
        foreach (var (id, column) in new[] { ("a", "Pending"), ("b", "Approved"), ("c", "Deferred"), ("d", "Rejected") })
            Assert.Single(view.FindAll($"[data-board-column='{column}'] [data-board-card='{id}']"));
        Assert.Single(view.FindAll(".finding-inspector"));
        view.Find("[aria-label='Board column']").Change("Deferred");
        Assert.Equal("c", state.FocusedId);
        view.Find("[data-board-card='a'] [data-finding-id='a']").Click();
        view.Find("input[type=search]").Input("a");
        Assert.Contains("1 hidden", view.Find(".queue-selection").TextContent);
        view.Find("[data-view='queue']").Click();
        Assert.Equal("a", state.FocusedId); Assert.Equal("a", state.Search);
        Assert.Equal(2, state.Selected.Count); Assert.Equal("keep my reason", state.Drafts["a"].Reason);
    }

    [Theory]
    [InlineData("Approved", FindingDecisionState.Approved)]
    [InlineData("Deferred", FindingDecisionState.Deferred)]
    [InlineData("Pending", FindingDecisionState.Pending)]
    public async Task Menu_moves_only_one_versioned_card_without_optimistic_placement(string target, FindingDecisionState expected)
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "b" }; state.Selected.UnionWith(["a", "b"]);
        var saves = new List<SetFindingDecisionRequest>();
        var gate = new TaskCompletionSource();
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.HeadSha, "head")
            .Add(x => x.Findings, new[] { Row("a", FindingDecisionState.Rejected), Row("b") })
            .Add(x => x.OnSave, async r => { saves.Add(r); await gate.Task; }));
        view.Find("[data-view='board']").Click();
        var move = view.Find("[aria-label='Move a to']").ChangeAsync(new() { Value = target });
        view.WaitForAssertion(() => Assert.Single(saves));
        view.WaitForAssertion(() => Assert.Contains("Saving", view.Find("[data-board-card='a']").TextContent));
        Assert.Single(view.FindAll("[data-board-column='Rejected'] [data-board-card='a']"));
        Assert.Equal(new SetFindingDecisionRequest("a", "head", expected, null, 3), Assert.Single(saves));
        var duplicate = view.InvokeAsync(() => view.Instance.ExecuteCommand("approve"));
        Assert.Single(saves);
        gate.SetResult(); await move; await duplicate;
        Assert.Equal(2, state.Selected.Count);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("canceled")]
    public async Task Completed_move_resets_selector_before_same_target_retry_even_without_a_new_decision_version(string outcome)
    {
        await using var c = new BunitContext();
        var state = new FindingWorkspaceState { FocusedId = "a" };
        var row = Row("a");
        var attempts = new List<(string Id, string Target)>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = c.Render<KeyObservingFindingBoard>(p => p.Add(x => x.State, state).Add(x => x.Findings, new[] { row })
            .Add(x => x.Active, true).Add(x => x.BlockReason, _ => null)
            .Add(x => x.OnMove, async (id, target) =>
            {
                attempts.Add((id, target));
                if (attempts.Count != 1) return;
                await gate.Task;
                if (outcome == "canceled") throw new OperationCanceledException();
                // A failed save may refresh authoritative data without changing its version.
            }));
        object SelectorKey() => view.Instance.MoveSelectorKey!;
        var original = view.Find("[aria-label='Move a to']");
        var originalKey = SelectorKey();
        var move = original.ChangeAsync(new() { Value = "Approved" });
        view.WaitForAssertion(() => Assert.Single(attempts));
        view.Render(p => p.Add(x => x.Findings, new[] { row }));
        Assert.Equal(originalKey, SelectorKey());
        Assert.Single(view.FindAll("[data-board-column='Pending'] [data-board-card='a']"));
        gate.SetResult();
        var error = await Record.ExceptionAsync(() => move);
        // Save failures are handled by the queue and return normally. Unhandled callback
        // exceptions terminate the Blazor circuit; they cannot support an in-circuit retry.
        // Blazor ignores canceled event-handler tasks; the selector must still reset.
        Assert.Null(error);
        view.WaitForAssertion(() => Assert.NotEqual(originalKey, SelectorKey()));

        // bUnit reparses DOM snapshots and does not model native selected-value change
        // suppression. A changed Blazor element key proves that the browser gets a new select.
        var reset = view.Find("[aria-label='Move a to']");
        var resetKey = SelectorKey();
        Assert.NotEqual(originalKey, resetKey);
        Assert.Equal("", reset.GetAttribute("value"));
        Assert.Null(state.Rejection);
        Assert.Single(view.FindAll("[data-board-column='Pending'] [data-board-card='a']"));
        await reset.ChangeAsync(new() { Value = "Approved" });
        Assert.Equal(new[] { ("a", "Approved"), ("a", "Approved") }, attempts);
        Assert.NotEqual(resetKey, SelectorKey());
    }

    // bUnit recreates its parsed DOM even when Blazor retains an element. Observe the
    // actual rendered key through a single-use component subclass, not DOM object identity.
#pragma warning disable BL0006 // Test-only inspection of Blazor's element replacement contract.
    public sealed class KeyObservingFindingBoard : FindingBoard
    {
        public object? MoveSelectorKey { get; private set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            base.BuildRenderTree(builder);
            var frames = builder.GetFrames();
            MoveSelectorKey = frames.Array.Take(frames.Count).Single(f => f.FrameType == RenderTreeFrameType.Element &&
                f.ElementName == "select" && f.ElementKey is not null).ElementKey;
        }
    }
#pragma warning restore BL0006

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Move_restores_connected_row_focus_after_authoritative_rerender_without_advancing(bool saved)
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var queue = c.JSInterop.SetupModule("./dashboard-queue.js");
        queue.SetupVoid("attach", _ => true).SetVoidResult();
        queue.SetupVoid("returnToRow", _ => true).SetVoidResult();
        var state = new FindingWorkspaceState { FocusedId = "a", Advance = false };
        var row = Row("a");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.HeadSha, "head")
            .Add(x => x.Findings, new[] { row }).Add(x => x.OnSave, async _ => await gate.Task));
        view.Find("[data-view='board']").Click();
        var move = view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved"));
        view.WaitForAssertion(() => Assert.Contains("Saving", view.Find("[data-board-card='a']").TextContent));
        var changed = saved ? row with { DecisionVersion = 4, Decision = new(Guid.NewGuid(), "a", "head", FindingDecisionState.Approved, null, "dashboard-session", DateTimeOffset.UtcNow, 4) } : row;
        view.Render(p => p.Add(x => x.Findings, new[] { changed }).Add(x => x.Disabled, !saved));
        gate.SetResult(); await move;
        view.WaitForAssertion(() => Assert.Contains(queue.Invocations, i => i.Identifier == "returnToRow" && Equals(i.Arguments[1], "a")));
        Assert.Equal("a", state.FocusedId);
        Assert.Single(view.FindAll($"[data-board-column='{(saved ? "Approved" : "Pending")}'].phone-active [data-finding-id='a']"));
    }

    [Fact]
    public void Phone_column_follows_same_identity_decision_change_but_preserves_intentional_empty_column_navigation()
    {
        using var c = new BunitContext();
        var state = new FindingWorkspaceState { FocusedId = "a" };
        var row = Row("a");
        var view = c.Render<FindingBoard>(p => p.Add(x => x.State, state).Add(x => x.Findings, new[] { row })
            .Add(x => x.Active, true).Add(x => x.BlockReason, _ => null).Add(x => x.OnMove, (_, _) => Task.CompletedTask));
        view.Find("[aria-label='Board column']").Change("Deferred");
        view.Render();
        Assert.Single(view.FindAll("[data-board-column='Deferred'].phone-active"));
        var changed = row with { Decision = new(Guid.NewGuid(), "a", "head", FindingDecisionState.Approved, null, "dashboard-session", DateTimeOffset.UtcNow, 4), DecisionVersion = 4 };
        view.Render(p => p.Add(x => x.Findings, new[] { changed }));
        Assert.Single(view.FindAll("[data-board-column='Approved'].phone-active [data-board-card='a']"));
    }

    [Fact]
    public async Task Drag_callback_reuses_menu_guards_and_rejection_retains_pinned_reason_on_conflict()
    {
        await using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "a" }; state.Selected.Add("b");
        var saves = new List<SetFindingDecisionRequest>(); var batches = new List<BatchFindingDecisionRequest>();
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.HeadSha, "head")
            .Add(x => x.Findings, new[] { Row("a"), Row("b") })
            .Add(x => x.OnSave, r => saves.Add(r)).Add(x => x.OnBatch, r => batches.Add(r)));
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved")); Assert.Empty(saves);
        view.Find("[data-view='board']").Click();
        await view.InvokeAsync(() => view.Instance.MoveCard("missing", "Approved"));
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "invalid"));
        Assert.Empty(saves);
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Rejected"));
        Assert.True(view.Find("[data-rejection-save]").HasAttribute("disabled"));
        view.Find("[aria-label='Shared rejection reason']").Input("keep this reason");
        view.Find("[data-rejection-save]").Click();
        Assert.Equal(new BatchFindingDecisionSelection("a", 3, FindingDecisionState.Rejected, "keep this reason"), Assert.Single(Assert.Single(batches).Selections));
        var composer = state.Rejection!;
        composer.Apply(new(null, [new("a", BatchFindingDecisionStatus.Conflict, 4)]));
        var changed = Row("a", FindingDecisionState.Deferred) with { DecisionVersion = 4 };
        view.Render(p => p.Add(x => x.Findings, new[] { changed, Row("b") }));
        Assert.Single(view.FindAll("[data-board-column='Deferred'] [data-board-card='a']"));
        Assert.Equal("keep this reason", composer.SharedReason);
        Assert.Equal(3, Assert.Single(composer.Entries).DecisionVersion);
        view.Find("[data-dialog-cancel]").Click(); view.Find("[data-rejection-discard]").Click();
        state.Drafts["a"] = new(3, FindingDecisionState.Rejected, "individual reason") { Dirty = true };
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved")); Assert.Empty(saves);
        Assert.Equal("individual reason", state.Drafts["a"].Reason);
        state.Drafts.Clear();
        view.Render(p => p.Add(x => x.Hidden, true));
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved")); Assert.Empty(saves);
        await view.InvokeAsync(() => view.Instance.ExecuteCommand("approve")); Assert.Empty(saves);
        view.Render(p => p.Add(x => x.Hidden, false).Add(x => x.Disabled, true));
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved")); Assert.Empty(saves);
        await view.InvokeAsync(() => view.Instance.DisposeAsync().AsTask());
        await view.InvokeAsync(() => view.Instance.MoveCard("a", "Approved")); Assert.Empty(saves);
    }

    [Fact]
    public void Rejected_move_uses_reason_composer_cancel_does_not_move_or_write_and_readonly_cannot_move()
    {
        using var c = new BunitContext(); c.JSInterop.Mode = JSRuntimeMode.Loose;
        var state = new FindingWorkspaceState { FocusedId = "a" }; state.Selected.Add("b");
        var saves = new List<SetFindingDecisionRequest>(); var batches = new List<BatchFindingDecisionRequest>();
        var view = c.Render<FindingQueue>(p => p.Add(x => x.State, state).Add(x => x.HeadSha, "head")
            .Add(x => x.Findings, new[] { Row("a"), Row("b", canDecide: false) })
            .Add(x => x.OnSave, r => saves.Add(r)).Add(x => x.OnBatch, r => batches.Add(r)));
        view.Find("[data-view='board']").Click();
        Assert.True(view.Find("[aria-label='Move b to']").HasAttribute("disabled"));
        view.Find("[aria-label='Move a to']").Change("Rejected");
        Assert.Single(state.Rejection!.Entries);
        Assert.Single(view.FindAll("[data-board-column='Pending'] [data-board-card='a']"));
        view.Find("[data-dialog-cancel]").Click();
        Assert.Null(state.Rejection); Assert.Empty(saves); Assert.Empty(batches);
        Assert.Equal("b", Assert.Single(state.Selected));
    }
}
