using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RejectionComposerStateTests
{
    private static RunFindingDto Finding(string id, long version = 3) => new(id, null, null, null, false, null, version, false, null, false, true);

    [Fact]
    public void Confirmation_copies_reasons_and_original_membership_not_changed_selection_or_reads()
    {
        var original = Finding("a");
        var workspace = new FindingWorkspaceState();
        workspace.Selected.Add("a");
        workspace.Rejection = new("head", [original]);
        var composer = workspace.Rejection;
        composer.SharedReason = " shared ";
        workspace.Selected.Clear(); workspace.Selected.Add("b");
        workspace.Search = "hidden"; workspace.Reconcile([original, Finding("b")]);
        var request = Assert.IsType<BatchFindingDecisionRequest>(composer.Confirm([original, Finding("b")], "head", false));
        composer.SharedReason = "changed after confirmation";
        Assert.Equal("a", Assert.Single(request.Selections).FindingId);
        Assert.Equal(3, request.Selections[0].ExpectedVersion);
        Assert.Equal("shared", request.Selections[0].Reason);
        Assert.Null(composer.Confirm([original], "head", false));
        Assert.True(composer.HasUnsaved);
    }

    [Fact]
    public void Refresh_cannot_replace_original_versions_or_revision_and_never_discards_reasons()
    {
        var composer = new RejectionComposerState("head", [Finding("a")]) { SharedReason = "retain" };
        Assert.Null(composer.Confirm([Finding("a", 4)], "head", false));
        Assert.Null(composer.Confirm([], "head", false));
        Assert.Null(composer.Confirm([Finding("a")], "new-head", false));
        Assert.Null(composer.Confirm([Finding("a")], "head", true));
        Assert.Equal("retain", composer.SharedReason);
        Assert.Equal(3, composer.Entries[0].DecisionVersion);
        Assert.False(composer.Attempted);
    }

    [Fact]
    public void All_overrides_allow_blank_shared_and_clearing_override_inherits_shared()
    {
        var composer = new RejectionComposerState("head", [Finding("a"), Finding("b")]);
        Assert.False(composer.HasUnsaved);
        composer.Overrides["a"] = " first "; composer.Overrides["b"] = " second ";
        Assert.Null(composer.BlockReason([Finding("a"), Finding("b")], "head", false));
        composer.Overrides["b"] = " ";
        Assert.NotNull(composer.Validation("b"));
        composer.SharedReason = " shared ";
        Assert.Equal("shared", composer.EffectiveReason("b"));
        Assert.Equal("first", composer.EffectiveReason("a"));
    }

    [Fact]
    public void Partial_outcomes_are_copied_saved_reasons_removed_unresolved_never_rebased()
    {
        var composer = new RejectionComposerState("head", [Finding("a"), Finding("b"), Finding("c")]) { SharedReason = "reason" };
        composer.Overrides["a"] = "saved override"; composer.Overrides["b"] = "conflict override";
        Assert.NotNull(composer.Confirm(composer.Entries, "head", false));
        var outcomes = new[] { new BatchFindingDecisionItem("a", BatchFindingDecisionStatus.Saved, 4), new("b", BatchFindingDecisionStatus.Conflict, 8), new("c", BatchFindingDecisionStatus.Unknown) };
        composer.Apply(new(null, outcomes));
        outcomes[0] = new("a", BatchFindingDecisionStatus.Unknown);
        Assert.Equal(BatchFindingDecisionStatus.Saved, composer.Outcomes[0].Status);
        Assert.Equal(new[] { "b", "c" }, composer.Entries.Select(f => f.FindingId));
        Assert.False(composer.Overrides.ContainsKey("a"));
        Assert.Equal("conflict override", composer.Overrides["b"]);
        Assert.All(composer.Entries, f => Assert.Equal(3, f.DecisionVersion));
        Assert.Null(composer.Confirm([Finding("b", 8), Finding("c")], "head", false));
    }

    [Fact]
    public void Fully_saved_batch_clears_unsaved_text_but_keeps_saved_outcomes()
    {
        var composer = new RejectionComposerState("head", [Finding("a")]) { SharedReason = "reason" };
        Assert.NotNull(composer.Confirm(composer.Entries, "head", false));
        composer.Apply(new(null, [new("a", BatchFindingDecisionStatus.Saved, 4)]));
        Assert.Empty(composer.Entries); Assert.Empty(composer.SharedReason); Assert.False(composer.HasUnsaved);
        Assert.Single(composer.Outcomes);
    }
}
