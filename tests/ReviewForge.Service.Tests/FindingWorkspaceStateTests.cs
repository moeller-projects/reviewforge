using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingWorkspaceStateTests
{
    private static RunFindingDto Row(string id, string severity, string file) => new(id,
        new("RULE", id, severity, "bug", "claim", null, null, new(file, 1, 2), false), null, null, false, null, 0, false, null, false, false);
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
