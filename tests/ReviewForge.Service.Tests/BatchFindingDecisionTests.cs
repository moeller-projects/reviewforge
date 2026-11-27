using System.Diagnostics.Metrics;
using ReviewForge.Core.Domain;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class BatchFindingDecisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ReviewRun Run() => new(Guid.NewGuid(), new("o", "p", "r", 1), "head", ReviewKind.Full,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true,
        Enumerable.Range(0, 4).Select(i => new StoredFinding($"f{i}", "rule", "high", "title", null, null, null, FindingJson: "{}")).ToArray(), Pipeline: "ReviewDraft");
    private static BatchFindingDecisionRequest Request(params BatchFindingDecisionSelection[] items) => new("head", items);
    private static BatchFindingDecisionSelection Item(int id, long version = 0, FindingDecisionState state = FindingDecisionState.Approved, string? reason = null) => new($"f{id}", version, state, reason);
    private static FindingDecisionService Service(FakeFindingStore store, InFlightClaims? claims = null) => new(store, store, TimeProvider.System, claims ?? new());

    [Fact]
    public async Task Mixed_results_are_independent_immutable_and_never_publish()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct);
        var service = Service(store);
        await service.DecideAsync(run.Id, "f0", "head", FindingDecisionState.Deferred, null, "header-key", 0, Ct);
        var result = await service.DecideBatchAsync(run.Id, Request(Item(0), Item(1, state: FindingDecisionState.Rejected), Item(2), Item(3, state: FindingDecisionState.Rejected, reason: "  reason  ")), "dashboard-session", Ct);
        Assert.Null(result.ErrorCode);
        Assert.Equal(new[] { BatchFindingDecisionStatus.Conflict, BatchFindingDecisionStatus.Invalid, BatchFindingDecisionStatus.Saved, BatchFindingDecisionStatus.Saved }, result.Items.Select(i => i.Status));
        Assert.Equal(1, result.Items[0].CurrentVersion);
        Assert.Equal("reason", result.Items[3].Decision!.Reason);
        Assert.Equal("dashboard-session", result.Items[2].Decision!.Actor);
        var reset = await service.DecideBatchAsync(run.Id, Request(Item(2, 1, FindingDecisionState.Pending)), "header-key", Ct);
        Assert.Equal(BatchFindingDecisionStatus.Saved, Assert.Single(reset.Items).Status);
        Assert.Equal(2, (await store.GetFindingDecisionHistoryAsync(run.Id, "f2", 100, Ct)).Count);
        Assert.All((await store.GetRunAsync(run.Id, Ct))!.Findings, f => Assert.Null(f.ThreadId));
    }

    [Fact]
    public async Task Malformed_envelopes_auth_and_run_failures_write_nothing()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); var service = Service(store);
        foreach (var request in new[] { Request(), Request(Item(0), Item(0)), Request(Item(0) with { FindingId = " " }), Request(Item(0)) with { HeadSha = "" }, Request(Item(0)) with { Selections = null! }, Request(null!), Request(Enumerable.Range(0, 501).Select(i => Item(i)).ToArray()) })
            Assert.Equal("invalid-selection", (await service.DecideBatchAsync(run.Id, request, "header-key", Ct)).ErrorCode);
        Assert.Equal("invalid-actor", (await service.DecideBatchAsync(run.Id, Request(Item(0)), "untrusted-name", Ct)).ErrorCode);
        Assert.Equal("run-not-found", (await service.DecideBatchAsync(Guid.NewGuid(), Request(Item(0)), "header-key", Ct)).ErrorCode);
        foreach (var invalid in new[] { run with { Pipeline = "Review" }, run with { Pipeline = "Resolve" }, run with { CompletedAt = null }, run with { Success = false } })
        {
            await store.SaveRunAsync(invalid, Ct);
            Assert.Equal("run-not-editable", (await service.DecideBatchAsync(run.Id, Request(Item(0)), "header-key", Ct)).ErrorCode);
        }
        await store.SaveRunAsync(run, Ct);
        Assert.Equal("revision-mismatch", (await service.DecideBatchAsync(run.Id, Request(Item(0)) with { HeadSha = "other" }, "header-key", Ct)).ErrorCode);
        Assert.Empty(await store.GetFindingDecisionsAsync(run.Id, Ct));
    }

    [Fact]
    public async Task Invalid_members_states_versions_and_reasons_do_not_block_valid_neighbors()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct);
        var result = await Service(store).DecideBatchAsync(run.Id, Request(Item(9), Item(0, -1), Item(1, long.MaxValue), Item(2, state: (FindingDecisionState)99), Item(3, reason: new string('x', 2001))), "header-key", Ct);
        Assert.All(result.Items, i => Assert.Equal(BatchFindingDecisionStatus.Invalid, i.Status));
        Assert.Empty(await store.GetFindingDecisionsAsync(run.Id, Ct));
    }

    [Fact]
    public async Task Contention_and_lost_claim_stop_without_replay_and_release_only_own_claim()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); var claims = new InFlightClaims(); var holder = Guid.NewGuid();
        claims.TryClaim(run.Pr, holder, out _);
        var service = Service(store, claims);
        var busy = await service.DecideBatchAsync(run.Id, Request(Item(0)), "header-key", Ct);
        Assert.Equal("busy", busy.ErrorCode); Assert.Equal(BatchFindingDecisionStatus.NotAttempted, Assert.Single(busy.Items).Status);
        claims.Release(run.Pr, holder);
        store.BeforeFindingDecisionAppend = async (_, _) =>
        {
            Assert.Equal(1, claims.ActiveCount);
            Assert.Equal(FindingDecisionStatus.Conflict, (await service.DecideAsync(run.Id, "f3", "head", FindingDecisionState.Approved, null, "header-key", 0, Ct)).Status);
            var owner = claims.Snapshot()[run.Pr]; claims.Release(run.Pr, owner); claims.TryClaim(run.Pr, holder, out _);
        };
        var lost = await service.DecideBatchAsync(run.Id, Request(Item(0), Item(1)), "header-key", Ct);
        Assert.Equal("claim-lost", lost.ErrorCode);
        Assert.Equal(new[] { BatchFindingDecisionStatus.Saved, BatchFindingDecisionStatus.NotAttempted }, lost.Items.Select(i => i.Status));
        Assert.True(claims.IsHeldBy(run.Pr, holder));
    }

    [Fact]
    public async Task Storage_failure_and_cancellation_preserve_confirmed_unknown_and_unattempted_outcomes()
    {
        foreach (var cancel in new[] { false, true })
        {
            var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); var claims = new InFlightClaims();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            store.BeforeFindingDecisionAppend = (d, _) =>
            {
                if (d.FindingId == "f1") { if (cancel) { stop.Cancel(); throw new OperationCanceledException(stop.Token); } throw new IOException("private failure"); }
                return Task.CompletedTask;
            };
            var result = await Service(store, claims).DecideBatchAsync(run.Id, Request(Item(0), Item(1), Item(2)), "header-key", stop.Token);
            Assert.Equal(cancel ? "cancelled" : "storage-failure", result.ErrorCode);
            Assert.Equal(new[] { BatchFindingDecisionStatus.Saved, BatchFindingDecisionStatus.Unknown, BatchFindingDecisionStatus.NotAttempted }, result.Items.Select(i => i.Status));
            Assert.Single(await store.GetFindingDecisionsAsync(run.Id, Ct)); Assert.Equal(0, claims.ActiveCount);
        }
    }

    [Theory]
    [InlineData("removed", "run-not-found")]
    [InlineData("ordinary", "run-not-editable")]
    [InlineData("revision", "revision-mismatch")]
    [InlineData("storage", "storage-failure")]
    public async Task Eligibility_is_reread_under_claim_before_any_append(string change, string error)
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); var claims = new InFlightClaims(); var reads = 0;
        store.BeforeGetRun = _ =>
        {
            if (++reads != 2) return;
            Assert.Equal(1, claims.ActiveCount);
            if (change == "storage") throw new IOException("private store error");
            store.Runs.Clear();
            if (change != "removed") store.Runs.Add(change == "ordinary" ? run with { Pipeline = "Review" } : run with { HeadSha = "next" });
        };
        var result = await Service(store, claims).DecideBatchAsync(run.Id, Request(Item(0)), "header-key", Ct);
        Assert.Equal(error, result.ErrorCode); Assert.Equal(BatchFindingDecisionStatus.NotAttempted, Assert.Single(result.Items).Status);
        Assert.Empty(await store.GetFindingDecisionsAsync(run.Id, Ct)); Assert.Equal(0, claims.ActiveCount);
    }

    [Fact]
    public async Task Ambiguous_append_can_have_committed_and_is_never_replayed()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct);
        store.BeforeFindingDecisionAppend = async (d, ct) =>
        {
            store.BeforeFindingDecisionAppend = null;
            await store.TryAppendFindingDecisionAsync(d, d.Version - 1, ct);
            throw new IOException("response lost after commit");
        };
        var result = await Service(store).DecideBatchAsync(run.Id, Request(Item(0), Item(1)), "header-key", Ct);
        Assert.Equal("storage-failure", result.ErrorCode);
        Assert.Equal(new[] { BatchFindingDecisionStatus.Unknown, BatchFindingDecisionStatus.NotAttempted }, result.Items.Select(i => i.Status));
        Assert.Single(await store.GetFindingDecisionHistoryAsync(run.Id, "f0", 100, Ct));
        Assert.Single(await store.GetFindingDecisionsAsync(run.Id, Ct));
    }

    [Fact]
    public async Task Heartbeat_renews_one_batch_claim_while_append_is_blocked()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); var claims = new InFlightClaims(ttl: TimeSpan.FromSeconds(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Name == "reviewforge.claims.renewed_total") l.EnableMeasurementEvents(instrument); };
        var batchContext = new AsyncLocal<bool>();
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { if (batchContext.Value) renewed.TrySetResult(); }); listener.Start();
        store.BeforeFindingDecisionAppend = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        batchContext.Value = true;
        var batch = Service(store, claims).DecideBatchAsync(run.Id, Request(Item(0), Item(1)), "header-key", Ct);
        batchContext.Value = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // The pre-append guard renews once; require a later timer-driven renewal.
            renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await renewed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(1, claims.ActiveCount); Assert.False(claims.TryClaim(run.Pr, Guid.NewGuid(), out _));
        }
        finally { release.TrySetResult(); }
        Assert.All((await batch).Items, i => Assert.Equal(BatchFindingDecisionStatus.Saved, i.Status)); Assert.Equal(0, claims.ActiveCount);
    }

    [Fact]
    public async Task Cancellation_during_append_leaves_committed_item_unknown_and_peers_unattempted()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); using var stop = new CancellationTokenSource();
        store.BeforeFindingDecisionAppend = async (d, _) =>
        {
            store.BeforeFindingDecisionAppend = null;
            await store.TryAppendFindingDecisionAsync(d, d.Version - 1, Ct);
            stop.Cancel();
        };
        var result = await Service(store).DecideBatchAsync(run.Id, Request(Item(0), Item(1)), "header-key", stop.Token);
        // The append observed cancellation, so the service correctly cannot certify its outcome.
        Assert.Equal("cancelled", result.ErrorCode); Assert.Equal(BatchFindingDecisionStatus.Unknown, result.Items[0].Status);
        Assert.Equal(BatchFindingDecisionStatus.NotAttempted, result.Items[1].Status); Assert.Single(await store.GetFindingDecisionsAsync(run.Id, Ct));
    }

    [Fact]
    public async Task Cancellation_before_processing_is_not_attempted()
    {
        var store = new FakeFindingStore(); var run = Run(); await store.SaveRunAsync(run, Ct); using var stop = new CancellationTokenSource(); stop.Cancel();
        var result = await Service(store).DecideBatchAsync(run.Id, Request(Item(0)), "header-key", stop.Token);
        Assert.Equal("cancelled", result.ErrorCode); Assert.Equal(BatchFindingDecisionStatus.NotAttempted, Assert.Single(result.Items).Status);
    }
}
