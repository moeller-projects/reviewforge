using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class FindingDecisionTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FindingDecisionState.Approved)]
    [InlineData(FindingDecisionState.Deferred)]
    [InlineData(FindingDecisionState.Rejected)]
    [InlineData(FindingDecisionState.Pending)]
    public void Decision_retains_revision_actor_reason_and_version(FindingDecisionState state)
    {
        var decision = FindingDecision.Record(RunId, "finding", "head", state,
            "  justification  ", "dashboard-session", Now, 3);

        Assert.Equal(RunId, decision.RunId);
        Assert.Equal("finding", decision.FindingId);
        Assert.Equal("head", decision.HeadSha);
        Assert.Equal(state, decision.State);
        Assert.Equal("justification", decision.Reason);
        Assert.Equal("dashboard-session", decision.Actor);
        Assert.Equal(Now, decision.DecidedAt);
        Assert.Equal(3, decision.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejection_requires_a_reason(string? reason)
    {
        Assert.Throws<ArgumentException>(() => FindingDecision.Record(RunId, "finding", "head",
            FindingDecisionState.Rejected, reason, "dashboard-session", Now, 1));
    }

    [Theory]
    [InlineData(FindingDecisionState.Approved)]
    [InlineData(FindingDecisionState.Deferred)]
    [InlineData(FindingDecisionState.Pending)]
    public void Other_decisions_allow_an_absent_reason(FindingDecisionState state)
    {
        Assert.Null(FindingDecision.Record(RunId, "finding", "head", state,
            "  ", "dashboard-session", Now, 1).Reason);
    }

    [Fact]
    public void Invalid_identity_state_version_and_excessive_text_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Record(runId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Record(findingId: " "));
        Assert.Throws<ArgumentException>(() => Record(headSha: " "));
        Assert.Throws<ArgumentException>(() => Record(actor: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(state: (FindingDecisionState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(version: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(version: -1));
        Assert.Throws<ArgumentException>(() => Record(reason: new string('x', FindingDecision.MaxReasonLength + 1)));
        Assert.Throws<ArgumentException>(() => Record(actor: new string('x', FindingDecision.MaxActorLength + 1)));
    }

    private static FindingDecision Record(Guid? runId = null, string findingId = "finding", string headSha = "head",
        FindingDecisionState state = FindingDecisionState.Approved, string? reason = null,
        string actor = "dashboard-session", long version = 1)
        => FindingDecision.Record(runId ?? RunId, findingId, headSha, state, reason, actor, Now, version);
}
