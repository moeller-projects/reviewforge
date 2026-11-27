using Microsoft.Extensions.Time.Testing;
using ReviewForge.Core.Domain;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingDecisionServiceTests
{
    private static ReviewRun Run() => new(Guid.NewGuid(), new("o", "p", "r", 1), "head", ReviewKind.Full,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], Pipeline: "ReviewDraft");
    private static FindingObservation Candidate() => FindingObservation.Create(new RichFinding
    {
        RuleId = "rule", Severity = "high", Category = "bug", Title = "candidate", Description = "claim", DedupeKey = "key",
    }) with { Validation = FindingValidationStatus.Rejected };

    [Fact]
    public async Task Rejected_candidate_can_be_decided_without_becoming_publishable_and_conflicts_return_current_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeFindingStore();
        var observation = Candidate();
        var run = Run() with { FindingObservations = [observation] };
        await store.SaveRunAsync(run, ct);
        var service = new FindingDecisionService(store, store, TimeProvider.System, new InFlightClaims());
        var id = observation.Id.ToString("N");
        var result = await service.DecideAsync(run.Id, id, "head", FindingDecisionState.Approved, null, "dashboard-session", 0, ct);
        Assert.Equal(FindingDecisionStatus.Saved, result.Status);
        Assert.Equal(1, result.CurrentVersion);
        var conflict = await service.DecideAsync(run.Id, id, "head", FindingDecisionState.Deferred, null, "header-key", 0, ct);
        Assert.Equal(FindingDecisionStatus.Conflict, conflict.Status);
        Assert.Equal(1, conflict.CurrentVersion);
        var finding = Assert.Single(FindingDecisionService.ResolveFindings(run));
        Assert.Equal(id, finding.Id);
        Assert.Equal(observation, finding.Observation);
        Assert.Null(finding.AcceptedFinding);
        Assert.False(finding.PublicationEligible);
        Assert.Empty(await store.GetKnownDedupeKeysAsync(run.Pr, ct));
    }

    [Fact]
    public async Task Invalid_mutations_have_no_decision_side_effects()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeFindingStore();
        var observation = Candidate();
        var run = Run() with { FindingObservations = [observation] };
        var service = new FindingDecisionService(store, store, TimeProvider.System, new InFlightClaims());
        async Task<FindingDecisionResult> Decide(ReviewRun r, string? id = null, string head = "head", string actor = "header-key",
            FindingDecisionState state = FindingDecisionState.Approved, long version = 0, string? reason = null)
        {
            await store.SaveRunAsync(r, ct);
            return await service.DecideAsync(r.Id, id ?? observation.Id.ToString("N"), head, state, reason, actor, version, ct);
        }
        Assert.Equal(FindingDecisionStatus.RunNotFound, (await service.DecideAsync(Guid.NewGuid(), "id", "head", FindingDecisionState.Pending, null, "header-key", 0, ct)).Status);
        foreach (var invalid in new[] { run with { Pipeline = "Review" }, run with { Pipeline = "Resolve" }, run with { Success = false }, run with { CompletedAt = null } })
            Assert.Equal(FindingDecisionStatus.RunNotEditable, (await Decide(invalid)).Status);
        Assert.Equal(FindingDecisionStatus.RevisionMismatch, (await Decide(run, head: "new-head")).Status);
        Assert.Equal(FindingDecisionStatus.FindingNotFound, (await Decide(run, id: "key")).Status);
        Assert.Equal(FindingDecisionStatus.FindingNotFound, (await Decide(run, id: Guid.NewGuid().ToString("N"))).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, actor: "")).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, state: FindingDecisionState.Rejected)).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, version: -1)).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, version: long.MaxValue)).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, state: (FindingDecisionState)99)).Status);
        Assert.Equal(FindingDecisionStatus.InvalidDecision, (await Decide(run, reason: new string('x', 2001))).Status);
        Assert.Empty(await store.GetFindingDecisionsAsync(run.Id, ct));
    }

    [Fact]
    public async Task Accepted_mapping_and_decision_history_keep_technical_and_human_state_separate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeFindingStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var observation = Candidate() with { Validation = FindingValidationStatus.Accepted };
        var row = new StoredFinding("key", "rule", "high", "title", null, null, null, FindingJson: "{}");
        var run = Run() with { FindingObservations = [observation], Findings = [row] };
        await store.SaveRunAsync(run, ct);
        var resolved = Assert.Single(FindingDecisionService.ResolveFindings(run));
        Assert.Same(row, resolved.AcceptedFinding);
        Assert.True(resolved.PublicationEligible);
        Assert.False(Assert.Single(FindingDecisionService.ResolveFindings(run with
        {
            FindingObservations = [observation with { Verification = FindingVerificationStatus.Rejected }],
        })).PublicationEligible);
        var service = new FindingDecisionService(store, store, clock, new InFlightClaims());
        var result = await service.DecideAsync(run.Id, resolved.Id, "head", FindingDecisionState.Rejected,
            "  Not useful  ", "dashboard-session", 0, ct);
        Assert.Equal("Not useful", result.Decision!.Reason);
        Assert.Equal(clock.GetUtcNow(), result.Decision.DecidedAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(FindingDecisionStatus.Saved, (await service.DecideAsync(run.Id, resolved.Id, "head",
            FindingDecisionState.Pending, null, "header-key", 1, ct)).Status);
        var history = await store.GetFindingDecisionHistoryAsync(run.Id, resolved.Id, 100, ct);
        Assert.Equal(new[] { FindingDecisionState.Pending, FindingDecisionState.Rejected }, history.Select(d => d.State));
        Assert.True(Assert.Single(FindingDecisionService.ResolveFindings(run)).PublicationEligible);
    }

    [Fact]
    public async Task Legacy_ids_require_full_rows_and_never_inherit_another_runs_decisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeFindingStore();
        var row = new StoredFinding("key", "rule", "high", "title", null, null, null, FindingJson: "{}");
        var run = Run() with { Findings = [row, row with { DedupeKey = "no-payload", FindingJson = null }, row with { DedupeKey = "thread-1" }] };
        await store.SaveRunAsync(run, ct);
        var service = new FindingDecisionService(store, store, TimeProvider.System, new InFlightClaims());
        Assert.Equal("key", Assert.Single(FindingDecisionService.ResolveFindings(run)).Id);
        Assert.Empty(FindingDecisionService.ResolveFindings(run with { FindingObservations = [] }));
        Assert.Equal(FindingDecisionStatus.Saved, (await service.DecideAsync(run.Id, "key", "head", FindingDecisionState.Rejected, "not useful", "header-key", 0, ct)).Status);
        var next = run with { Id = Guid.NewGuid(), HeadSha = "next" };
        await store.SaveRunAsync(next, ct);
        Assert.Empty(await store.GetFindingDecisionsAsync(next.Id, ct));
        Assert.Equal(FindingDecisionStatus.Saved, (await service.DecideAsync(next.Id, "key", "next", FindingDecisionState.Pending, null, "header-key", 0, ct)).Status);
    }
}
