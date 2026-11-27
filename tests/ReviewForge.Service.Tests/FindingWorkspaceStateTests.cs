using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingWorkspaceStateTests
{
    private static RunFindingDto Row(string id, string severity, string file) => new(id,
        new("RULE", id, severity, "bug", "claim", null, null, new(file, 1, 2), false), null, null, false, null, 0, false, null, false, false);
    [Fact]
    public void Batch_snapshot_is_bounded_read_only_filter_independent_and_preserves_drafts()
    {
        var state = new FindingWorkspaceState { Search = "hidden" };
        var a = Row("a", "high", "a.cs") with { CanDecide = true, DecisionVersion = 7 };
        Assert.Null(state.SnapshotBatch([a], "head", FindingDecisionState.Approved, false));
        state.Selected.Add("a");
        var snapshot = state.SnapshotBatch([a], "head", FindingDecisionState.Approved, false)!;
        state.Selected.Clear(); Assert.Equal("a", Assert.Single(snapshot.Selections).FindingId); Assert.Equal(7, snapshot.Selections[0].ExpectedVersion);
        state.Selected.Add("a");
        Assert.Null(state.SnapshotBatch([a], "head", FindingDecisionState.Rejected, false));
        Assert.Null(state.SnapshotBatch([a], "head", FindingDecisionState.Approved, true));
        Assert.Null(state.SnapshotBatch([a], "", FindingDecisionState.Approved, false));
        Assert.Null(state.SnapshotBatch([a with { CanDecide = false }], "head", FindingDecisionState.Approved, false));
        Assert.Null(state.SnapshotBatch([], "head", FindingDecisionState.Approved, false));
        state.Drafts["a"] = new(7, FindingDecisionState.Rejected, "unsaved") { Dirty = true };
        Assert.Null(state.SnapshotBatch([a], "head", FindingDecisionState.Deferred, false)); Assert.Equal("unsaved", state.Drafts["a"].Reason);
        state.Drafts.Clear(); foreach (var i in Enumerable.Range(0, 500)) state.Selected.Add($"extra{i}");
        Assert.Null(state.SnapshotBatch([a], "head", FindingDecisionState.Pending, false));
    }

    [Fact]
    public void Confirmed_decision_advances_in_snapshot_order_only_to_current_matching_pending()
    {
        var a = Row("a", "high", "a.cs"); var b = Row("b", "high", "b.cs"); var c = Row("c", "high", "c.cs");
        var state = new FindingWorkspaceState { FocusedId = "b" };
        state.AdvanceAfterSave("b", ["a", "b", "c"], [a, b, c]);
        Assert.Equal("c", state.FocusedId); Assert.False(state.Complete);
        state.AdvanceAfterSave("c", ["a", "b", "c"], [a, b, c]);
        Assert.Equal("a", state.FocusedId);
        state.Search = "not matched";
        state.AdvanceAfterSave("a", ["a", "b", "c"], [a, b, c]);
        Assert.True(state.Complete); Assert.Equal("a", state.FocusedId); Assert.Equal("not matched", state.Search);
        state.Advance = false; state.Complete = false;
        state.AdvanceAfterSave("a", ["a", "b"], [a, b]); Assert.Equal("a", state.FocusedId); Assert.False(state.Complete);
    }
    [Fact]
    public void Resetting_only_finding_to_pending_does_not_announce_exhaustion()
    {
        var finding = Row("a", "high", "a.cs");
        var state = new FindingWorkspaceState { FocusedId = "a" };
        state.AdvanceAfterSave("a", ["a"], [finding]);
        Assert.False(state.Complete);
        Assert.Equal("a", state.FocusedId);
    }

    [Fact]
    public void Decision_guard_is_shared_and_preserves_unsaved_or_conflicting_drafts()
    {
        var row = Row("a", "high", "a.cs") with { CanDecide = true };
        var state = new FindingWorkspaceState();
        Assert.Null(state.DecisionBlockReason(row, "head", false));
        Assert.NotNull(state.DecisionBlockReason(row, "head", true));
        Assert.NotNull(state.DecisionBlockReason(row, "", false));
        Assert.NotNull(state.DecisionBlockReason(row with { CanDecide = false }, "head", false));
        state.Drafts["a"] = new(99, FindingDecisionState.Rejected, "retained") { Dirty = true };
        Assert.NotNull(state.DecisionBlockReason(row, "head", false));
    }
    [Fact]
    public void Filters_order_and_range_use_identity_not_input_positions()
    {
        var a = Row("a", "high", "a.cs"); var b = Row("b", "critical", "z.cs"); var c = Row("c", "low", "c.cs");
        var state = new FindingWorkspaceState();
        RunFindingDto[] rows = [c, b, a];
        Assert.Equal(new[] { "b", "a", "c" }, state.Matching(rows).Select(f => f.FindingId));
        state.Select("b", true, false, state.Matching(rows)); state.Select("c", true, true, state.Matching(rows));
        Assert.Equal(3, state.Selected.Count);
        state.Select("a", false, true, state.Matching(rows)); Assert.Single(state.Selected);
        state.Select("b", false, false, state.Matching(rows)); Assert.Empty(state.Selected);
        state.Sort = "file"; Assert.Equal("a", state.Matching(rows)[0].FindingId);
        state.Search = "RULE"; Assert.Equal(3, state.Matching(rows).Count);
        state.Search = "z.cs"; Assert.Equal("b", Assert.Single(state.Matching(rows)).FindingId);
        state.Search = ""; state.Severity = "critical"; Assert.Single(state.Matching(rows));
        state.Severity = "all"; state.Pipeline = "Unavailable"; Assert.Equal(3, state.Matching(rows).Count);
        state.Publication = "Unknown"; Assert.Empty(state.Matching(rows));
        state.Publication = "Previously known"; Assert.Empty(state.Matching(rows));
        state.Publication = "all"; state.Decision = "Pending"; Assert.Equal(3, state.Matching(rows).Count);
        state.Selected.Add("gone"); state.FocusedId = "gone"; state.Reconcile(rows); Assert.Empty(state.Selected); Assert.Equal("c", state.FocusedId);
        state.Reconcile([]); Assert.Null(state.FocusedId);
        Assert.Null(FindingWorkspaceState.Payload(a with { Candidate = null }));
        Assert.Equal("Pipeline-accepted", FindingWorkspaceState.PipelineLabel(a with { AcceptedFinding = a.Candidate }));
        Assert.Equal("Pipeline-rejected", FindingWorkspaceState.PipelineLabel(a with { Evidence = new(FindingValidationStatus.Rejected, "", "", FindingVerificationStatus.Rejected, "", "") }));
        foreach (var severity in new[] { "medium", "info", "other" }) Assert.Single(new FindingWorkspaceState().Matching([Row("x", severity, "x")]));
    }
}
