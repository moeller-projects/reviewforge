using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class DraftReviewServiceTests
{
    private static readonly PrKey Key = new("org", "project", "repo", 1);
    private readonly FakeFindingStore _Store = new();
    private readonly FakePullRequestSource _Source = new();
    private readonly InFlightClaims _Claims = new();
    private readonly ReviewQueue _Queue = new();
    private readonly RunTracker _Tracker = new();
    private readonly DraftReviewService _Service;

    public DraftReviewServiceTests()
    {
        var runs = new RunSubmissionService(_Queue, _Tracker, _Claims, _Store,
            TimeProvider.System, NullLogger<RunSubmissionService>.Instance);
        _Service = new DraftReviewService(_Store, _Source, _Claims, runs, _Store);
    }

    [Fact]
    public async Task Query_returns_full_findings_only_for_successful_completed_drafts()
    {
        var run = Draft([Finding("key-1")]);
        _Store.Runs.Add(run);

        var result = await _Service.GetFindingsAsync(run.Id, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("head-sha", result.ReviewedHeadSha);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("key-1", finding.FindingId);
        Assert.Equal("description", finding.Description);
        Assert.Equal("src/file.cs", finding.Anchor!.FilePath);
    }

    [Fact]
    public async Task Query_rejects_unknown_non_draft_running_and_failed_runs()
    {
        Assert.False((await _Service.GetFindingsAsync(Guid.NewGuid(), CancellationToken.None)).Success);
        var ordinary = Draft([]) with {Pipeline = nameof(RunKind.Review)};
        var running = Draft([]) with {CompletedAt = null};
        var failed = Draft([]) with {Success = false};
        var failedShell = Draft([]) with {CompletedAt = null, Success = false};
        _Store.Runs.AddRange([ordinary, running, failed, failedShell]);
        _Tracker.Set(failedShell.Id, Key, RunState.Failed, kind: RunKind.ReviewDraft);

        Assert.Contains("not a review draft", (await _Service.GetFindingsAsync(ordinary.Id, CancellationToken.None)).Error);
        Assert.Contains("still running", (await _Service.GetFindingsAsync(running.Id, CancellationToken.None)).Error);
        Assert.Contains("failed", (await _Service.GetFindingsAsync(failed.Id, CancellationToken.None)).Error);
        Assert.Contains("failed", (await _Service.GetFindingsAsync(failedShell.Id, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Query_uses_run_tracker_for_drafts_not_yet_persisted()
    {
        var runId = Guid.NewGuid();
        var queued = _Queue.TryEnqueue(new ReviewRequest(runId, Key, DateTimeOffset.UtcNow,
            Trigger: EnqueueTrigger.Manual, Kind: RunKind.ReviewDraft));
        Assert.True(queued.Accepted);
        _Tracker.Set(runId, Key, RunState.Queued, kind: RunKind.ReviewDraft);

        var result = await _Service.GetFindingsAsync(runId, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("still running", result.Error);
    }

    [Fact]
    public async Task Publish_rejects_stale_head_before_writing()
    {
        var run = Draft([Finding("key-1")]) with {HeadSha = "old-sha"};
        _Store.Runs.Add(run);

        var result = await _Service.PublishAsync(run.Id, null, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("head changed", result.Error);
        Assert.Empty(_Source.PostedFindings);
    }

    [Fact]
    public async Task Publish_rejects_invalid_run_and_selection_before_writing()
    {
        Assert.Contains("unknown run", (await _Service.PublishAsync(Guid.NewGuid(), null, CancellationToken.None)).Error);
        var ordinary = Draft([Finding("key-1")]) with {Pipeline = nameof(RunKind.Review)};
        var running = Draft([Finding("key-1")]) with {CompletedAt = null};
        var failed = Draft([Finding("key-1")]) with {Success = false};
        _Store.Runs.AddRange([ordinary, running, failed]);
        Assert.Contains("not a review draft", (await _Service.PublishAsync(ordinary.Id, null, CancellationToken.None)).Error);
        Assert.Contains("still running", (await _Service.PublishAsync(running.Id, null, CancellationToken.None)).Error);
        Assert.Contains("failed", (await _Service.PublishAsync(failed.Id, null, CancellationToken.None)).Error);

        var draft = Draft([Finding("key-1")]);
        _Store.Runs.Add(draft);
        Assert.Contains("duplicates", (await _Service.PublishAsync(draft.Id, ["key-1", "key-1"], CancellationToken.None)).Error);
        Assert.Contains("do not belong", (await _Service.PublishAsync(draft.Id, ["missing"], CancellationToken.None)).Error);
        Assert.Empty(_Source.PostedFindings);
    }

    [Fact]
    public async Task Publish_rejects_when_pr_is_already_claimed()
    {
        var run = Draft([Finding("key-1")]);
        _Store.Runs.Add(run);
        Assert.True(_Claims.TryClaim(Key, Guid.NewGuid(), out _));

        var result = await _Service.PublishAsync(run.Id, null, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("already in flight", result.Error);
        Assert.Empty(_Source.PostedFindings);
    }

    [Fact]
    public async Task Publish_reconciles_partial_failure_without_reposting_an_ambiguous_finding()
    {
        var run = Draft([Finding("key-1"), Finding("key-2")]);
        _Store.Runs.Add(run);
        _Source.ThrowOnPostKey = "key-2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _Service.PublishAsync(run.Id, null, CancellationToken.None));
        Assert.Single(_Source.PostedFindings);

        _Source.ThrowOnPostKey = null;
        var unknown = await _Service.PublishAsync(run.Id, null, CancellationToken.None);
        Assert.False(unknown.Accepted);
        Assert.Equal(0, unknown.PublishedCount);
        Assert.Single(_Source.PostedFindings);
        _Source.Threads.Add(new(77, "key-2", ReviewThreadStatus.Active, []));
        var retry = await _Service.PublishAsync(run.Id, null, CancellationToken.None);

        Assert.True(retry.Accepted);
        Assert.Equal(0, retry.PublishedCount);
        Assert.Equal(2, retry.AlreadyPublishedCount);
        Assert.Single(_Source.PostedFindings);
    }

    [Fact]
    public async Task Publish_uses_general_comment_for_downgraded_anchor_and_marks_existing_thread()
    {
        var downgraded = Finding("general-key") with {AnchorDowngraded = true};
        var existing = Finding("existing-key");
        var run = Draft([downgraded, existing]);
        _Store.Runs.Add(run);
        _Source.Threads.Add(new ReviewThread(77, "existing-key", ReviewThreadStatus.Active, []));

        var result = await _Service.PublishAsync(run.Id, null, CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Single(_Source.GeneralComments);
        Assert.Equal("general-key", Assert.Single(_Source.GeneralCommentDedupeKeys));
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(1, result.AlreadyPublishedCount);
        Assert.True(_Store.Runs.Single().Findings[0].Published);
        Assert.NotNull(_Store.Runs.Single().Findings[0].ThreadId);
        var persisted = Assert.Single(_Store.Runs.Single().Findings, f => f.DedupeKey == "existing-key");
        Assert.True(persisted.Published);
        Assert.Equal(77, persisted.ThreadId);
    }

    [Fact]
    public async Task Technician_requires_explicit_unique_known_approved_current_selection()
    {
        var run = Draft([Finding("one"), Finding("two")]);
        _Store.Runs.Add(run);
        async Task<PublishSelectedDraftResult> Publish(PublishSelectedDraftRequest request) =>
            await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal("selection-required", (await Publish(new("head-sha", null))).ErrorCode);
        Assert.Equal("selection-required", (await Publish(new("head-sha", []))).ErrorCode);
        Assert.Equal("invalid-selection", (await Publish(new("head-sha", [new("one", 1), new("one", 1)]))).ErrorCode);
        Assert.Equal("invalid-selection", (await Publish(new("head-sha", [new("other-run", 1)]))).ErrorCode);
        Assert.Equal("approval-conflict", (await Publish(new("head-sha", [new("one", 1)]))).ErrorCode);
        await Approve(run, "one");
        Assert.Equal("approval-conflict", (await Publish(new("head-sha", [new("one", 2)]))).ErrorCode);
        Assert.Equal("approval-conflict", (await Publish(new("head-sha", [new("one", 1), new("two", 1)]))).ErrorCode);
        Assert.Empty(_Source.PostedFindings);
        Assert.Equal("head-changed", (await Publish(new("old", [new("one", 1)]))).ErrorCode);
        _Source.Pr = _Source.Pr with { SourceCommitSha = "new-head" };
        Assert.Equal("head-changed", (await Publish(new("head-sha", [new("one", 1)]))).ErrorCode);
        Assert.Empty(_Source.PostedFindings);
        _Source.Pr = _Source.Pr with { SourceCommitSha = "head-sha" };
        var posted = await Publish(new("head-sha", [new("one", 1)]));
        Assert.True(posted.Accepted);
        Assert.Equal(1, posted.PostedCount);
        Assert.Equal(PublishFindingStatus.Posted, Assert.Single(posted.Findings).Status);
        Assert.Equal("one", Assert.Single(_Source.PostedFindings).Finding.DedupeKey);
        Assert.Equal(1, (await Publish(new("head-sha", [new("one", 1)]))).AlreadyPostedCount);
    }

    [Fact]
    public async Task Technician_observation_mapping_cannot_override_technical_rejection()
    {
        var f = Finding("one");
        var observation = FindingObservation.Create(f) with { Validation = FindingValidationStatus.Rejected };
        var run = Draft([f]) with { FindingObservations = [observation] };
        _Store.Runs.Add(run);
        var id = observation.Id.ToString("N");
        await Approve(run, id);
        var result = await _Service.PublishSelectedAsync(run.Id, new("head-sha", [new(id, 1)]), CancellationToken.None);
        Assert.Equal("ineligible-finding", result.ErrorCode);
        Assert.Equal(PublishFindingStatus.NotAttempted, Assert.Single(result.Findings).Status);
        Assert.Empty(_Source.PostedFindings);
    }

    [Fact]
    public async Task Technician_partial_failure_returns_unknown_and_retry_reconciles_without_new_post_count()
    {
        var fs = new[] { Finding("one"), Finding("two"), Finding("three") };
        var os = fs.Select(f => FindingObservation.Create(f) with { Validation = FindingValidationStatus.Accepted }).ToArray();
        var run = Draft(fs) with { FindingObservations = os };
        _Store.Runs.Add(run);
        foreach (var o in os) await Approve(run, o.Id.ToString("N"));
        var request = new PublishSelectedDraftRequest("head-sha", os.Select(o => new PublishFindingSelection(o.Id.ToString("N"), 1)).ToArray());
        _Source.ThrowOnPostKey = "two";
        var result = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Equal(new[] { PublishFindingStatus.Posted, PublishFindingStatus.Unknown, PublishFindingStatus.NotAttempted }, result.Findings.Select(f => f.Status));
        Assert.Equal("publication-unknown", result.ErrorCode);
        Assert.True(_Store.Runs.Single().FindingObservations![0].NewlyPosted);
        _Source.ThrowOnPostKey = null;
        _Source.Threads.Add(new(77, "two", ReviewThreadStatus.Active, []));
        var retry = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal(1, retry.PostedCount);
        Assert.Equal(2, retry.AlreadyPostedCount);
        var latest = _Store.Runs.Single();
        Assert.False(latest.FindingObservations![1].NewlyPosted);
        Assert.Equal(2, FindingObservationCounts.From(latest.FindingObservations!).NewlyPosted);
        Assert.Equal(77, latest.Findings[1].ThreadId);
    }

    [Fact]
    public async Task Publication_and_decisions_share_claim_and_audit_survives_browser_cancellation()
    {
        var f = Finding("one") with { AnchorDowngraded = true };
        var run = Draft([f]) with { FindingObservations = [FindingObservation.Create(f) with { Validation = FindingValidationStatus.Accepted }] };
        _Store.Runs.Add(run);
        var id = run.FindingObservations![0].Id.ToString("N");
        await Approve(run, id);
        var claim = Guid.NewGuid();
        Assert.True(_Claims.TryClaim(Key, claim, out _));
        var request = new PublishSelectedDraftRequest("head-sha", [new(id, 1)]);
        Assert.Equal("busy", (await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None)).ErrorCode);
        _Claims.Release(Key, claim);
        using var browser = new CancellationTokenSource();
        _Source.AfterPost = () =>
        {
            var mutation = new FindingDecisionService(_Store, _Store, TimeProvider.System, _Claims)
                .DecideAsync(run.Id, id, run.HeadSha, FindingDecisionState.Rejected, "no", "dashboard-session", 1, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(FindingDecisionStatus.Conflict, mutation.Status);
            browser.Cancel();
        };
        var result = await _Service.PublishSelectedAsync(run.Id, request, browser.Token);
        Assert.Equal(1, result.PostedCount);
        var latest = _Store.Runs.Single();
        Assert.True(latest.Findings[0].Published);
        Assert.NotNull(latest.Findings[0].ThreadId);
        Assert.True(latest.FindingObservations![0].NewlyPosted);
        Assert.Empty(_Source.Votes);
        Assert.Empty(_Source.Replies);
        Assert.Empty(_Source.StatusChanges);
    }

    [Fact]
    public async Task Technician_rejects_invalid_run_states_and_changed_approval_without_posts()
    {
        Assert.Equal("run-not-found", (await _Service.PublishSelectedAsync(Guid.NewGuid(), new("head-sha", [new("one", 1)]), CancellationToken.None)).ErrorCode);
        var queued = Guid.NewGuid();
        _Tracker.Set(queued, Key, RunState.Queued, kind: RunKind.ReviewDraft);
        Assert.Equal("run-not-publishable", (await _Service.PublishSelectedAsync(queued, new("head-sha", [new("one", 1)]), CancellationToken.None)).ErrorCode);
        foreach (var run in new[] { Draft([Finding("one")]) with { Pipeline = "Review" }, Draft([Finding("one")]) with { CompletedAt = null }, Draft([Finding("one")]) with { Success = false } })
        {
            _Store.Runs.Add(run);
            Assert.Equal("run-not-publishable", (await _Service.PublishSelectedAsync(run.Id, new("head-sha", [new("one", 1)]), CancellationToken.None)).ErrorCode);
        }
        var failedStatus = Draft([Finding("one")]);
        _Store.Runs.Add(failedStatus);
        _Tracker.Set(failedStatus.Id, Key, RunState.Failed, kind: RunKind.ReviewDraft);
        Assert.Equal("run-not-publishable", (await _Service.PublishSelectedAsync(failedStatus.Id, new("head-sha", [new("one", 1)]), CancellationToken.None)).ErrorCode);
        var rejected = Draft([Finding("one")]);
        _Store.Runs.Add(rejected);
        await Approve(rejected, "one");
        await _Store.TryAppendFindingDecisionAsync(FindingDecision.Record(rejected.Id, "one", rejected.HeadSha,
            FindingDecisionState.Rejected, "not useful", "dashboard-session", DateTimeOffset.UtcNow, 2), 1, CancellationToken.None);
        Assert.Equal("approval-conflict", (await _Service.PublishSelectedAsync(rejected.Id, new("head-sha", [new("one", 2)]), CancellationToken.None)).ErrorCode);
        Assert.Empty(_Source.PostedFindings);
    }

    [Fact]
    public async Task Technician_rechecks_head_before_each_write_and_reports_prewrite_failure_safely()
    {
        var run = Draft([Finding("one"), Finding("two")]);
        _Store.Runs.Add(run);
        await Approve(run, "one");
        await Approve(run, "two");
        var request = new PublishSelectedDraftRequest("head-sha", [new("one", 1), new("two", 1)]);
        _Source.ThrowOnGetPullRequest = new InvalidOperationException("SECRET");
        var failed = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal("publication-failed", failed.ErrorCode);
        Assert.Equal(1, failed.FailedCount);
        Assert.Equal(1, failed.NotAttemptedCount);
        _Source.ThrowOnGetPullRequest = null;
        _Source.AfterPost = () => _Source.Pr = _Source.Pr with { SourceCommitSha = "changed" };
        var partial = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal("head-changed", partial.ErrorCode);
        Assert.Equal(1, partial.PostedCount);
        Assert.Equal(1, partial.NotAttemptedCount);
        Assert.Single(_Source.PostedFindings);
    }

    [Fact]
    public async Task Technician_lost_claim_leaves_remaining_findings_unattempted()
    {
        var run = Draft([Finding("one"), Finding("two")]);
        _Store.Runs.Add(run);
        await Approve(run, "one");
        await Approve(run, "two");
        var otherClaim = Guid.NewGuid();
        _Source.AfterPost = () =>
        {
            _Claims.Release(Key, _Claims.Snapshot()[Key]);
            Assert.True(_Claims.TryClaim(Key, otherClaim, out _));
        };
        var result = await _Service.PublishSelectedAsync(run.Id,
            new("head-sha", [new("one", 1), new("two", 1)]), CancellationToken.None);
        Assert.Equal("busy", result.ErrorCode);
        Assert.Equal(1, result.PostedCount);
        Assert.Equal(1, result.NotAttemptedCount);
        Assert.Single(_Source.PostedFindings);
        Assert.True(_Claims.IsHeldBy(Key, otherClaim));
    }

    [Fact]
    public async Task Publication_and_new_post_attribution_are_atomic_even_when_separate_snapshot_save_would_fail()
    {
        var f = Finding("one");
        var run = Draft([f]) with { FindingObservations = [FindingObservation.Create(f) with { Validation = FindingValidationStatus.Accepted }] };
        _Store.Runs.Add(run);
        var id = run.FindingObservations![0].Id.ToString("N");
        await Approve(run, id);
        _Source.AfterPost = () => _Store.ThrowOnSave = new InvalidOperationException("SECRET");
        var request = new PublishSelectedDraftRequest("head-sha", [new(id, 1)]);
        var result = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.True(result.Accepted);
        Assert.Equal(1, result.PostedCount);
        Assert.NotNull(Assert.Single(result.Findings).ThreadId);
        Assert.True(_Store.Runs.Single().Findings[0].Published);
        Assert.True(Assert.Single(_Store.Runs.Single().FindingObservations!).NewlyPosted);
        _Store.ThrowOnSave = null;
        Assert.Equal(1, (await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None)).AlreadyPostedCount);
        Assert.True(Assert.Single(_Store.Runs.Single().FindingObservations!).NewlyPosted);
        Assert.Single(_Source.PostedFindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_host_write_has_durable_intent_and_explicit_reconciliation_never_reposts(bool captured)
    {
        var finding = Finding("ambiguous");
        var run = Draft([finding]) with { FindingObservations = captured
            ? [FindingObservation.Create(finding) with { Validation = FindingValidationStatus.Accepted }] : null };
        _Store.Runs.Add(run);
        var id = captured ? run.FindingObservations![0].Id.ToString("N") : "ambiguous";
        await Approve(run, id);
        _Source.AfterPost = () => throw new InvalidOperationException("private transport failure");
        var outcome = await _Service.PublishSelectedAsync(run.Id, new("head-sha", [new(id, 1)]), CancellationToken.None);
        Assert.Equal(1, outcome.UnknownCount);
        Assert.True(_Store.Runs.Single().Findings[0].PublicationAttempted);
        if (captured) Assert.True(Assert.Single(_Store.Runs.Single().FindingObservations!).PublicationAttempted);
        else Assert.Null(_Store.Runs.Single().FindingObservations);
        Assert.False(_Store.Runs.Single().Findings[0].Published);
        _Source.AfterPost = null;
        var repeated = await _Service.PublishSelectedAsync(run.Id, new("head-sha", [new(id, 1)]), CancellationToken.None);
        Assert.False(repeated.Accepted);
        Assert.Equal(1, repeated.UnknownCount);
        Assert.Single(_Source.PostedFindings);
        var notObserved = await _Service.ReconcileSelectedAsync(run.Id, new([id]), CancellationToken.None);
        Assert.Equal(1, notObserved.UnknownCount);
        Assert.Single(_Source.PostedFindings);
        var posted = _Source.PostedFindings[0];
        _Source.Threads.Add(new(posted.ThreadId, "ambiguous", ReviewThreadStatus.Active, []));
        _Source.Pr = _Source.Pr with { SourceCommitSha = "later-head" };
        var reconciled = await _Service.ReconcileSelectedAsync(run.Id, new([id]), CancellationToken.None);
        Assert.True(reconciled.Accepted);
        Assert.Equal(1, reconciled.AlreadyPostedCount);
        Assert.True(_Store.Runs.Single().Findings[0].Published);
        Assert.Single(_Source.PostedFindings);
        Assert.Empty(_Source.GeneralComments);
    }

    [Theory]
    [InlineData(false, "fetch-failed")]
    [InlineData(true, "fetch-failed")]
    [InlineData(false, "head-changed")]
    [InlineData(true, "head-changed")]
    [InlineData(false, "claim-lost")]
    [InlineData(true, "claim-lost")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    public async Task Definitive_prewrite_exit_clears_intent_and_allows_an_explicit_retry(bool captured, string failure)
    {
        var finding = Finding("one");
        var run = Draft([finding]) with { FindingObservations = captured
            ? [FindingObservation.Create(finding) with { Validation = FindingValidationStatus.Accepted }] : null };
        _Store.Runs.Add(run);
        var id = captured ? run.FindingObservations![0].Id.ToString("N") : "one";
        await Approve(run, id);
        var request = new PublishSelectedDraftRequest("head-sha", [new(id, 1)]);
        using var browser = new CancellationTokenSource();
        var otherClaim = Guid.NewGuid();
        var fetches = 0;
        _Source.BeforeGetPullRequest = () =>
        {
            if (++fetches != 3) return; // The pin check after durable intent has been written.
            Assert.True(_Store.Runs.Single().Findings[0].PublicationAttempted);
            switch (failure)
            {
                case "fetch-failed": _Source.ThrowOnGetPullRequest = new InvalidOperationException("private failure"); break;
                case "head-changed": _Source.Pr = _Source.Pr with { SourceCommitSha = "other-head" }; break;
                case "claim-lost":
                    _Claims.Release(Key, _Claims.Snapshot()[Key]);
                    Assert.True(_Claims.TryClaim(Key, otherClaim, out _));
                    break;
                case "cancelled":
                    browser.Cancel();
                    _Source.ThrowOnGetPullRequest = new OperationCanceledException(browser.Token);
                    break;
            }
        };
        var failed = await _Service.PublishSelectedAsync(run.Id, request, browser.Token);
        Assert.False(failed.Accepted);
        Assert.Equal(failure switch { "head-changed" => "head-changed", "claim-lost" => "busy", _ => "publication-failed" }, failed.ErrorCode);
        Assert.Empty(_Source.PostedFindings);
        var saved = _Store.Runs.Single();
        Assert.False(saved.Findings[0].PublicationAttempted);
        if (captured) Assert.False(Assert.Single(saved.FindingObservations!).PublicationAttempted);
        else Assert.Null(saved.FindingObservations);
        if (failure == "claim-lost") Assert.True(_Claims.IsHeldBy(Key, otherClaim));
        _Claims.Release(Key, otherClaim);
        _Source.BeforeGetPullRequest = null;
        _Source.ThrowOnGetPullRequest = null;
        _Source.Pr = _Source.Pr with { SourceCommitSha = "head-sha" };
        var retry = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.True(retry.Accepted);
        Assert.Equal(1, retry.PostedCount);
        Assert.Single(_Source.PostedFindings);
    }

    [Fact]
    public async Task Intent_storage_failure_prevents_host_write_and_is_retryable_after_recovery()
    {
        var run = Draft([Finding("one")]);
        _Store.Runs.Add(run);
        await Approve(run, "one");
        var request = new PublishSelectedDraftRequest("head-sha", [new("one", 1)]);
        _Store.ThrowOnPublicationIntent = new InvalidOperationException("private store failure");
        var failed = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal("publication-unknown", failed.ErrorCode);
        Assert.Equal(PublishFindingStatus.Unknown, Assert.Single(failed.Findings).Status);
        Assert.Empty(_Source.PostedFindings);
        Assert.False(_Store.Runs.Single().Findings[0].PublicationAttempted);
        _Store.ThrowOnPublicationIntent = null;
        Assert.Equal(1, (await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None)).PostedCount);
        Assert.Single(_Source.PostedFindings);
    }

    [Fact]
    public async Task Failed_prewrite_cleanup_retains_conservative_intent_and_never_replays()
    {
        var run = Draft([Finding("one")]);
        _Store.Runs.Add(run);
        await Approve(run, "one");
        var request = new PublishSelectedDraftRequest("head-sha", [new("one", 1)]);
        var fetches = 0;
        _Source.BeforeGetPullRequest = () =>
        {
            if (++fetches != 3) return;
            _Store.ThrowOnPublicationIntent = new InvalidOperationException("cleanup unavailable");
            _Source.ThrowOnGetPullRequest = new InvalidOperationException("prewrite failure");
        };
        var failed = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal("publication-unknown", failed.ErrorCode);
        Assert.Equal(PublishFindingStatus.Unknown, Assert.Single(failed.Findings).Status);
        Assert.True(_Store.Runs.Single().Findings[0].PublicationAttempted);
        Assert.Empty(_Source.PostedFindings);
        _Store.ThrowOnPublicationIntent = null;
        _Source.BeforeGetPullRequest = null;
        _Source.ThrowOnGetPullRequest = null;
        var retry = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal(1, retry.UnknownCount);
        Assert.Empty(_Source.PostedFindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Later_cleanup_failure_preserves_confirmed_publication_and_unattempted_peers(bool captured)
    {
        var findings = new[] { Finding("one"), Finding("two"), Finding("three") };
        var run = Draft(findings) with { FindingObservations = captured
            ? findings.Select(f => FindingObservation.Create(f) with { Validation = FindingValidationStatus.Accepted }).ToArray() : null };
        _Store.Runs.Add(run);
        var ids = captured ? run.FindingObservations!.Select(o => o.Id.ToString("N")).ToArray() : ["one", "two", "three"];
        foreach (var id in ids) await Approve(run, id);
        var request = new PublishSelectedDraftRequest("head-sha", ids.Select(id => new PublishFindingSelection(id, 1)).ToArray());
        var fetches = 0;
        _Source.BeforeGetPullRequest = () =>
        {
            if (++fetches != 5) return; // Second finding's pin check, after its durable intent.
            Assert.True(_Store.Runs.Single().Findings[0].Published);
            Assert.True(_Store.Runs.Single().Findings[1].PublicationAttempted);
            _Store.ThrowOnPublicationIntent = new InvalidOperationException("cleanup unavailable");
            _Source.ThrowOnGetPullRequest = new InvalidOperationException("prewrite failure");
        };
        var result = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.False(result.Accepted);
        Assert.Equal("publication-unknown", result.ErrorCode);
        Assert.Equal(new[] { PublishFindingStatus.Posted, PublishFindingStatus.Unknown, PublishFindingStatus.NotAttempted }, result.Findings.Select(f => f.Status));
        Assert.Equal(1, result.PostedCount);
        Assert.Equal(1, result.UnknownCount);
        Assert.Equal(1, result.NotAttemptedCount);
        Assert.Equal(_Source.PostedFindings[0].ThreadId, result.Findings[0].ThreadId);
        Assert.Single(_Source.PostedFindings);
        var saved = _Store.Runs.Single();
        Assert.True(saved.Findings[0].Published);
        Assert.True(saved.Findings[1].PublicationAttempted);
        Assert.False(saved.Findings[2].PublicationAttempted);
        if (!captured) Assert.Null(saved.FindingObservations);
        _Store.ThrowOnPublicationIntent = null;
        _Source.BeforeGetPullRequest = null;
        _Source.ThrowOnGetPullRequest = null;
        var retry = await _Service.PublishSelectedAsync(run.Id, request, CancellationToken.None);
        Assert.Equal(new[] { PublishFindingStatus.AlreadyPosted, PublishFindingStatus.Unknown, PublishFindingStatus.Posted }, retry.Findings.Select(f => f.Status));
        Assert.Equal(new[] { "one", "three" }, _Source.PostedFindings.Select(p => p.Finding.DedupeKey));
    }

    [Fact]
    public async Task Malformed_payload_is_rejected_before_publication_or_intent()
    {
        var run = Draft([Finding("one")]);
        run = run with { Findings = [run.Findings[0] with { FindingJson = "invalid-json" }] };
        _Store.Runs.Add(run);
        await Approve(run, "one");
        var result = await _Service.PublishSelectedAsync(run.Id, new("head-sha", [new("one", 1)]), CancellationToken.None);
        Assert.Equal("ineligible-finding", result.ErrorCode);
        Assert.Equal(PublishFindingStatus.NotAttempted, Assert.Single(result.Findings).Status);
        Assert.False(_Store.Runs.Single().Findings[0].PublicationAttempted);
        Assert.Empty(_Source.PostedFindings);
    }

    private async Task Approve(ReviewRun run, string id) => Assert.True(await _Store.TryAppendFindingDecisionAsync(
        FindingDecision.Record(run.Id, id, run.HeadSha, FindingDecisionState.Approved, null, "dashboard-session", DateTimeOffset.UtcNow, 1), 0, CancellationToken.None));

    private static ReviewRun Draft(IReadOnlyList<RichFinding> findings)
    {
        var now = DateTimeOffset.UtcNow;
        return new ReviewRun(Guid.NewGuid(), Key, "head-sha", ReviewKind.Full,
            now.AddMinutes(-1), now, true,
            findings.Select(f => new StoredFinding(
                    f.DedupeKey!, f.RuleId, f.Severity, f.Title, f.Anchor?.FilePath, f.Anchor?.StartLine,
                    null, FindingJson: JsonSerializer.Serialize(f, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
                .ToArray(),
            Pipeline: nameof(RunKind.ReviewDraft));
    }

    private static RichFinding Finding(string key) => new()
    {
        RuleId = "test/rule",
        Title = key,
        Severity = "high",
        Category = "bug",
        Description = "description",
        Snippet = "snippet",
        Suggestion = "suggestion",
        Anchor = new FindingAnchor("src/file.cs", 4, 4),
        DedupeKey = key,
    };
}