using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using ReviewForge.Infrastructure.Persistence;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class DashboardQueryServiceTests
{
    private readonly FakePullRequestSource _Source = new();
    private readonly FakeFindingStore _Store = new();
    private readonly ReviewQueue _Queue = new();
    private readonly RunTracker _Tracker = new();
    private readonly InFlightClaims _Claims = new();
    private readonly PrKey _Pr = new("org", "project", "repo", 1);
    private readonly DateTimeOffset _Now = DateTimeOffset.UtcNow;

    private async Task<DashboardRow> Row()
    {
        _Source.Inventory = new([new(new(_Pr, _Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
        var cache = new DashboardInventoryCache(_Source, TimeProvider.System);
        return Assert.Single((await new DashboardQueryService(cache, _Tracker, _Queue, _Claims, _Store,
            TimeProvider.System).GetAsync(TestContext.Current.CancellationToken)).Rows);
    }

    [Theory]
    [InlineData("PR is a draft", "Draft PR — review skipped.")]
    [InlineData("head already reviewed and no new human comments", "Head already reviewed; no new human comments.")]
    [InlineData("bot-authored head", "Bot-authored head — review skipped.")]
    [InlineData("claim lost while queued", "Claim lost while queued.")]
    [InlineData("secret exception /private/path", "Review skipped.")]
    public async Task Outcome_details_are_allowlisted(string detail, string safe)
    {
        var id = Guid.NewGuid();
        _Tracker.Set(id, _Pr, RunState.Running);
        _Tracker.CaptureHead(id, "head-sha");
        _Tracker.Set(id, _Pr, RunState.Skipped, detail);
        Assert.Equal(safe, (await Row()).LatestAttempt!.OutcomeDetail);
        _Tracker.Set(id, _Pr, RunState.Failed, "secret exception /private/path");
        Assert.Equal("Review failed. See service logs for investigation.", (await Row()).LatestAttempt!.OutcomeDetail);
    }

    private ReviewRun Run(string head = "head-sha", bool success = true, int minutes = -10, string pipeline = "Review")
        => new(Guid.NewGuid(), _Pr, head, ReviewKind.Full, _Now.AddMinutes(minutes), _Now.AddMinutes(minutes + 1), success, [], Pipeline: pipeline);

    [Fact]
    public async Task Large_inventory_uses_one_batch_port_read_and_cached_source_but_fresh_operational_state()
    {
        _Source.Inventory = new(Enumerable.Range(1, 501).Select(i => new PullRequestInventoryItem(
            new(_Pr with { PrId = i }, _Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")).ToArray(), true, []);
        var cache = new DashboardInventoryCache(_Source, TimeProvider.System);
        var service = new DashboardQueryService(cache, _Tracker, _Queue, _Claims, _Store, TimeProvider.System);
        var first = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(501, first.Rows.Count);
        Assert.Equal(1, _Store.SummaryReads);
        _Queue.TryEnqueue(new(Guid.NewGuid(), _Pr, _Now));
        var second = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.False(second.Rows[0].CanRerun);
        Assert.Equal(2, _Store.SummaryReads);
        Assert.Equal(1, _Source.InventoryFetches);
        Assert.Equal(0, _Source.ThreadFetches);
    }

    [Theory]
    [InlineData("Resolve")]
    [InlineData("ReviewDraft")]
    [InlineData("0")]
    public async Task Non_review_persisted_success_is_not_current_head_coverage(string pipeline)
    {
        _Store.Runs.Add(Run(pipeline: pipeline));
        var row = await Row();
        Assert.False(row.CurrentHeadReviewed);
        Assert.Null(row.LatestAttempt);
        Assert.True(row.CanRerun);
    }

    [Fact]
    public async Task No_run_and_old_head_success_are_unreviewed()
    {
        Assert.False((await Row()).CurrentHeadReviewed);
        _Store.Runs.Add(Run("old"));
        var row = await Row();
        Assert.False(row.CurrentHeadReviewed);
        Assert.True(row.CanRerun);
        Assert.Null(row.LatestAttempt);
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Skipped)]
    public async Task Failed_or_skipped_rerun_retains_success_and_hides_exception_details(RunState state)
    {
        var success = Run();
        _Store.Runs.Add(success);
        var request = new ReviewRequest(Guid.NewGuid(), _Pr, _Now, HeadSha: "head-sha");
        _Tracker.Register(request);
        _Tracker.Set(request.RunId, _Pr, state, "secret exception /private/path");
        var row = await Row();
        Assert.True(row.CurrentHeadReviewed);
        Assert.Equal(success.Id, row.LastSuccessfulReview!.RunId);
        Assert.Equal(state, row.LatestAttempt!.State);
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.DoesNotContain("secret exception", json);
        Assert.DoesNotContain("/private/path", json);
        Assert.True(row.CanRerun);
    }

    [Fact]
    public async Task Reconciles_run_ids_live_state_wins_and_persisted_counts_enrich()
    {
        var run = Run();
        _Store.Runs.Add(run with { Findings = [new("finding", "rule", "Warning", "title", null, null, null)] });
        _Tracker.Register(new(run.Id, _Pr, run.StartedAt, HeadSha: run.HeadSha));
        _Tracker.Set(run.Id, _Pr, RunState.Failed);
        var row = await Row();
        Assert.False(row.CurrentHeadReviewed);
        Assert.Equal(1, row.LatestAttempt!.ReviewFindings);
        Assert.Equal(RunState.Failed, row.LatestAttempt.State);
        Assert.Null(row.LastSuccessfulReview);
    }

    [Theory]
    [InlineData(null, RunKind.Review)]
    [InlineData("old", RunKind.Review)]
    [InlineData("head-sha", RunKind.Resolve)]
    [InlineData("head-sha", RunKind.ReviewDraft)]
    public async Task Pending_work_is_a_pr_wide_blocker_not_coverage(string? head, RunKind kind)
    {
        var request = new ReviewRequest(Guid.NewGuid(), _Pr, _Now, HeadSha: head, Kind: kind, ForceReview: kind == RunKind.Review);
        _Queue.TryEnqueue(request);
        var row = await Row();
        Assert.False(row.CurrentHeadReviewed);
        Assert.False(row.CanRerun);
        Assert.Null(row.LatestAttempt);
        Assert.Equal(request.RunId, row.ActiveBlocker!.RunId);
        Assert.Equal(head, row.ActiveBlocker.HeadSha);
        Assert.Equal(kind, row.ActiveBlocker.Kind);
    }

    [Fact]
    public async Task Durable_claim_without_tracker_blocks_but_does_not_fabricate_running_or_queued_progress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rf-dashboard-{Guid.NewGuid():N}.db");
        try
        {
            var queue = new SqliteReviewQueue($"Data Source={path};Pooling=False");
            var request = new ReviewRequest(Guid.NewGuid(), _Pr, _Now, HeadSha: "head-sha");
            queue.TryEnqueue(request);
            await using (var reader = queue.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
                Assert.True(await reader.MoveNextAsync());
            _Source.Inventory = new([new(new(_Pr, _Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
            var service = new DashboardQueryService(new(_Source, TimeProvider.System), _Tracker,
                queue, _Claims, _Store, TimeProvider.System);
            var row = Assert.Single((await service.GetAsync(TestContext.Current.CancellationToken)).Rows);
            Assert.False(row.CanRerun);
            Assert.Equal(request.RunId, row.ActiveBlocker!.RunId);
            Assert.True(row.ActiveBlocker.IsClaimed);
            Assert.Null(row.ActiveBlocker.State);
            Assert.Null(row.ActiveBlocker.ProgressPercent);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Pending_queue_row_is_still_blocking_when_persisted_shell_has_no_tracker()
    {
        var run = Run() with { CompletedAt = null, Success = false };
        _Store.Runs.Add(run);
        _Queue.TryEnqueue(new(run.Id, _Pr, _Now, HeadSha: run.HeadSha));
        var row = await Row();
        Assert.False(row.CanRerun);
        Assert.Equal(run.Id, row.ActiveBlocker!.RunId);
        Assert.Null(row.LatestAttempt!.State); // shell is not proof of resumed execution
        Assert.True(row.LatestAttempt.IsIncomplete);
    }

    [Fact]
    public async Task Claim_without_tracker_blocks_and_expired_release_does_not_change_ownership()
    {
        var id = Guid.NewGuid();
        _Claims.TryClaim(_Pr, id, out _);
        var row = await Row();
        Assert.False(row.CanRerun);
        Assert.Equal(id, row.ActiveBlocker!.RunId);
        Assert.Null(row.ActiveBlocker.Kind);
        Assert.True(_Claims.IsHeldBy(_Pr, id));
        _Claims.Release(_Pr, id);
        Assert.True((await Row()).CanRerun);
    }

    [Theory]
    [InlineData(null, RunState.Failed)]
    [InlineData("another-head", RunState.Completed)]
    public async Task Terminal_run_identity_is_observable_independently_of_current_head(string? head, RunState terminal)
    {
        var request = new ReviewRequest(Guid.NewGuid(), _Pr, _Now, HeadSha: head);
        _Tracker.Register(request);
        _Tracker.Set(request.RunId, _Pr, terminal);
        _Source.Inventory = new([new(new(_Pr, _Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
        var service = new DashboardQueryService(new(_Source, TimeProvider.System), _Tracker, _Queue, _Claims, _Store, TimeProvider.System);
        var response = await service.GetAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(response.Rows);
        Assert.Null(row.LatestAttempt);
        Assert.False(row.CurrentHeadReviewed);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(row);
        Assert.True(json.TryGetProperty("LatestPrAttempt", out var latest));
        Assert.Equal(request.RunId, latest.GetProperty("RunId").GetGuid());
        Assert.Equal(terminal, response.RunStates[request.RunId]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_terminal_observations_do_not_expand_details_and_corrections_reveal_older_success(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"rf-bounded-{Guid.NewGuid():N}.db");
        try
        {
            IFindingStore store = sqlite ? new SqliteFindingStore($"Data Source={path};Pooling=False") : _Store;
            var success = Run(minutes: -1000);
            await store.SaveRunAsync(success, ct);
            for (var i = 0; i < 200; i++)
            {
                var corrected = Run(minutes: -900 + i);
                await store.SaveRunAsync(corrected, ct); // durable success is superseded by tracker failure
                _Tracker.Register(new(corrected.Id, _Pr, corrected.StartedAt, HeadSha: corrected.HeadSha));
                _Tracker.Set(corrected.Id, _Pr, RunState.Failed);
            }
            _Source.Inventory = new([new(new(_Pr, _Source.Pr, "main", "creator", "Creator"), "Repo", "https://example.com")], true, []);
            var response = await new DashboardQueryService(new(_Source, TimeProvider.System), _Tracker, _Queue, _Claims,
                store, TimeProvider.System).GetAsync(ct);
            var row = Assert.Single(response.Rows);
            Assert.Equal(success.Id, row.LastSuccessfulReview!.RunId);
            Assert.Equal(RunState.Failed, row.LatestAttempt!.State);
            Assert.Equal(201, response.RunStates.Count);
            Assert.True(row.CurrentHeadReviewed);
            if (!sqlite)
            {
                Assert.Equal(200, _Store.SummaryObservedIds.Count);
                Assert.Single(_Store.SummaryDetailIds);
                Assert.Equal(2, _Store.SummaryMaterialized);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task Unselected_retained_terminal_still_corrects_an_older_pending_queue_copy()
    {
        var old = new ReviewRequest(Guid.NewGuid(), _Pr, _Now.AddHours(-2), HeadSha: "old");
        _Queue.TryEnqueue(old);
        _Tracker.Register(old);
        _Tracker.Set(old.RunId, _Pr, RunState.Completed);
        var latest = new ReviewRequest(Guid.NewGuid(), _Pr, _Now, HeadSha: "head-sha");
        _Tracker.Register(latest);
        _Tracker.Set(latest.RunId, _Pr, RunState.Completed);
        var row = await Row();
        Assert.True(row.CanRerun);
        Assert.Null(row.ActiveBlocker);
        Assert.Equal(latest.RunId, row.LatestPrAttempt!.RunId);
        Assert.DoesNotContain(old.RunId, _Store.SummaryDetailIds);
    }

    [Fact]
    public async Task Persisted_different_head_latest_attempt_does_not_replace_current_head_coverage_or_attempt()
    {
        var current = Run(minutes: -20);
        var other = Run(head: "different", minutes: -1);
        _Store.Runs.AddRange([current, other]);
        var row = await Row();
        Assert.Equal(other.Id, row.LatestPrAttempt!.RunId);
        Assert.Equal(current.Id, row.LatestAttempt!.RunId);
        Assert.Equal(current.Id, row.LastSuccessfulReview!.RunId);
    }

    [Fact]
    public async Task Early_fetch_failure_retains_safe_stage_and_unknown_head_as_latest_pr_attempt()
    {
        var request = new ReviewRequest(Guid.NewGuid(), _Pr, _Now);
        _Tracker.Register(request);
        _Tracker.DefinitionLoaded(request.RunId, [StageId.FetchPrContext, StageId.ReviewGate]);
        _Tracker.StageStarting(request.RunId, StageId.FetchPrContext);
        _Tracker.Set(request.RunId, _Pr, RunState.Failed, "secret /private/path");
        _Store.Runs.Add(Run(head: "") with { Id = request.RunId, Success = false });
        var row = await Row();
        Assert.Equal("FetchPrContext", row.LatestPrAttempt!.CurrentStage);
        Assert.Null(row.LatestPrAttempt.HeadSha);
        Assert.Equal("Review failed. See service logs for investigation.", row.LatestPrAttempt.OutcomeDetail);
        Assert.Null(row.LatestAttempt);
        Assert.Null(row.LastSuccessfulReview);
    }

    [Fact]
    public async Task Current_head_active_attempt_wins_without_using_updated_at_and_terminal_queue_is_not_resurrected()
    {
        var completed = Run(minutes: -1);
        _Store.Runs.Add(completed);
        var active = new ReviewRequest(Guid.NewGuid(), _Pr, _Now.AddMinutes(-20), HeadSha: "head-sha");
        _Tracker.Register(active);
        _Tracker.Set(active.RunId, _Pr, RunState.Running);
        _Tracker.DefinitionLoaded(active.RunId, [StageId.FetchPrContext, StageId.ReviewGate]);
        _Tracker.StageStarting(active.RunId, StageId.ReviewGate);
        var row = await Row();
        Assert.Equal(active.RunId, row.LatestAttempt!.RunId);
        Assert.Equal(50, row.LatestAttempt.ProgressPercent);
        Assert.Equal(new[] { "FetchPrContext", "ReviewGate" }, row.LatestAttempt.LoadedStages);
        Assert.Equal("ReviewGate", row.LatestAttempt.CurrentStage);
        Assert.Equal(_Tracker.Get(active.RunId)!.UpdatedAt, row.LatestAttempt.UpdatedAt);
        Assert.True(row.CurrentHeadReviewed);
        _Queue.TryEnqueue(active);
        _Tracker.Set(active.RunId, _Pr, RunState.Completed);
        row = await Row();
        Assert.Equal(completed.Id, row.LatestAttempt!.RunId);
        Assert.True(row.CanRerun);
        Assert.Null(row.ActiveBlocker);
    }
}
