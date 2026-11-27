using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class RunDetailsServiceTests
{
    [Theory]
    [InlineData("Review", true)]
    [InlineData("ReviewDraft", true)]
    [InlineData("Review", false)]
    [InlineData("ReviewDraft", false)]
    public async Task Captured_details_include_payload_evidence_decision_and_publication_without_confidence(string pipeline, bool success)
    {
        var store = new FakeFindingStore();
        var tracker = new RunTracker();
        var queue = new ReviewQueue();
        var runs = new RunSubmissionService(queue, tracker, new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store);
        var finding = new RichFinding { RuleId = "r", Title = "title", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var o = FindingObservation.Create(finding) with { Validation = FindingValidationStatus.Accepted,
            Verification = FindingVerificationStatus.Confirmed, VerificationReason = "evidence", VerificationProvenance = "lightweight-verifier/fast", NewlyPosted = true };
        var run = new ReviewRun(Guid.NewGuid(), new("o", "p", "r", 1), "head", ReviewKind.Full, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, success, [new("key", "r", "high", "title", null, null, 42,
                FindingJson: JsonSerializer.Serialize(finding, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Published: true)], Pipeline: pipeline, FindingObservations: [o]);
        store.Runs.Add(run);
        var decision = FindingDecision.Record(run.Id, o.Id.ToString("N"), "head", FindingDecisionState.Deferred, "later", "dashboard-session", DateTimeOffset.UtcNow, 1);
        Assert.True(await store.TryAppendFindingDecisionAsync(decision, 0, TestContext.Current.CancellationToken));
        var details = (await query.GetAsync(run.Id, TestContext.Current.CancellationToken))!;
        var row = Assert.Single(details.Findings);
        Assert.Equal("claim", row.Candidate!.Description);
        Assert.Equal("claim", row.AcceptedFinding!.Description);
        Assert.Equal("lightweight-verifier/fast", row.Evidence!.VerificationProvenance);
        Assert.True(row.Published);
        Assert.Equal(42, row.ThreadId);
        Assert.Equal(1, row.DecisionVersion);
        Assert.Equal(FindingDecisionState.Deferred, row.Decision!.State);
        Assert.Equal(success && pipeline == "ReviewDraft", row.CanDecide);
        Assert.Equal(new FindingObservationCounts(1, 1, 0, 0, 1), details.Counts);
        Assert.DoesNotContain("confidence", JsonSerializer.Serialize(details), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Durable_diagnostics_survive_tracker_miss_and_legacy_is_unavailable()
    {
        var store = new FakeFindingStore();
        var runs = new RunSubmissionService(new ReviewQueue(), new RunTracker(), new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store);
        using var ctx = new ReviewForge.Core.Pipeline.ReviewContext(new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        var run = new ReviewRun(ctx.RunId, ctx.Pr, "head", ReviewKind.Full, ctx.StartedAt, DateTimeOffset.UtcNow, true, []);
        await store.SaveRunAsync(run, TestContext.Current.CancellationToken);
        Assert.Null((await query.GetAsync(run.Id, TestContext.Current.CancellationToken))!.Diagnostics);
        await store.UpdateRunDiagnosticsAsync(run.Id, ctx.SnapshotDiagnostics(), TestContext.Current.CancellationToken);
        Assert.Equal(run.Id, (await query.GetAsync(run.Id, TestContext.Current.CancellationToken))!.Diagnostics!.RunId);
    }

    [Fact]
    public async Task History_is_member_checked_bounded_and_redacts_unrecognized_actor_identity()
    {
        var store = new FakeFindingStore();
        var runs = new RunSubmissionService(new ReviewQueue(), new RunTracker(), new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store);
        var f = new RichFinding { RuleId = "r", Title = "t", Severity = "high", Category = "bug", Description = "c", DedupeKey = "key" };
        var o = FindingObservation.Create(f);
        var id = Guid.NewGuid();
        var key = o.Id.ToString("N");
        store.Runs.Add(new(id, new("o", "p", "r", 1), "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], Pipeline: "ReviewDraft", FindingObservations: [o]));
        for (var v = 1; v <= 105; v++)
            Assert.True(await store.TryAppendFindingDecisionAsync(FindingDecision.Record(id, key, "head", FindingDecisionState.Deferred, "reason", "private-session-identity", DateTimeOffset.UtcNow, v), v - 1, TestContext.Current.CancellationToken));
        var response = (await query.GetHistoryAsync(id, key, TestContext.Current.CancellationToken))!;
        Assert.Equal(100, response.Events.Count);
        Assert.Equal(105, response.Events[0].Version);
        Assert.All(response.Events, d => Assert.Equal("unavailable", d.Actor));
        Assert.Null(await query.GetHistoryAsync(id, "not-a-member", TestContext.Current.CancellationToken));
        Assert.Null(await query.GetHistoryAsync(Guid.NewGuid(), key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Optional_source_metadata_enriches_but_never_replaces_durable_details()
    {
        var store = new FakeFindingStore();
        var source = new FakePullRequestSource { ThrowOnGetInventory = new InvalidOperationException("private details") };
        using var cache = new DashboardInventoryCache(source, TimeProvider.System);
        var runs = new RunSubmissionService(new ReviewQueue(), new RunTracker(), new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store, new RunSourceMetadataService(cache));
        var id = Guid.NewGuid();
        store.Runs.Add(new(id, new("o", "p", "r", 1), "reviewed", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, [], FindingObservations: []));
        var response = (await query.GetAsync(id, TestContext.Current.CancellationToken))!;
        Assert.Equal("reviewed", response.ReviewedHeadSha);
        Assert.Null(response.Source!.CurrentHeadSha);
        Assert.NotNull(response.Source.Warning);
        Assert.True(response.ObservationsAvailable);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task Durable_publication_intent_is_unknown_until_thread_audit_is_confirmed()
    {
        var store = new FakeFindingStore();
        var runs = new RunSubmissionService(new ReviewQueue(), new RunTracker(), new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store);
        var finding = new RichFinding { RuleId = "r", Title = "title", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var observation = FindingObservation.Create(finding) with { Validation = FindingValidationStatus.Accepted, PublicationAttempted = true };
        var run = new ReviewRun(Guid.NewGuid(), new("o", "p", "r", 1), "head", ReviewKind.Full, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, true, [new("key", "r", "high", "title", null, null, null,
                FindingJson: JsonSerializer.Serialize(finding, new JsonSerializerOptions(JsonSerializerDefaults.Web)))],
            Pipeline: "ReviewDraft", FindingObservations: [observation]);
        store.Runs.Add(run);
        var unknown = Assert.Single((await query.GetAsync(run.Id, TestContext.Current.CancellationToken))!.Findings);
        Assert.Null(unknown.Published);
        Assert.True(unknown.PublicationUnknown);
        await store.RecordFindingPublicationAsync(run.Id, "key", 42, false, TestContext.Current.CancellationToken);
        var posted = Assert.Single((await query.GetAsync(run.Id, TestContext.Current.CancellationToken))!.Findings);
        Assert.True(posted.Published);
        Assert.False(posted.PublicationUnknown);
    }

    [Fact]
    public async Task Queue_readthrough_running_unknown_empty_and_corrupt_legacy_are_honest()
    {
        var store = new FakeFindingStore();
        var queue = new ReviewQueue();
        var runs = new RunSubmissionService(queue, new RunTracker(), new InFlightClaims(), store, TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        var query = new RunDetailsService(runs, store, store);
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await query.GetAsync(Guid.NewGuid(), ct));
        var id = Guid.NewGuid();
        Assert.True(queue.TryEnqueue(new(id, new("o", "p", "r", 1), DateTimeOffset.UtcNow, Kind: RunKind.ReviewDraft)).Accepted);
        var queued = (await query.GetAsync(id, ct))!;
        Assert.Equal(RunState.Queued, queued.Status.State);
        Assert.Null(queued.ReviewedHeadSha);
        queue.Acknowledge(id);
        store.Runs.Add(new(id, new("o", "p", "r", 1), "", ReviewKind.Full, DateTimeOffset.UtcNow, null, false, [], FindingObservations: []));
        var running = (await query.GetAsync(id, ct))!;
        Assert.Equal(RunState.Running, running.Status.State);
        Assert.True(running.ObservationsAvailable);
        Assert.Equal(0, running.Counts!.Candidates);
        store.Runs[0] = store.Runs[0] with { FindingObservations = null,
            Findings = [new("key", "r", "high", "t", null, null, null, FindingJson: "invalid-json")] };
        var legacy = (await query.GetAsync(id, ct))!;
        Assert.Null(legacy.Counts);
        var row = Assert.Single(legacy.Findings);
        Assert.Null(row.AcceptedFinding);
        Assert.Null(row.Published);
    }
}
