using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;
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
        _Service = new DraftReviewService(_Store, _Source, _Claims, runs);
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
    public async Task Publish_retries_after_partial_failure_without_reposting_completed_finding()
    {
        var run = Draft([Finding("key-1"), Finding("key-2")]);
        _Store.Runs.Add(run);
        _Source.ThrowOnPostKey = "key-2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _Service.PublishAsync(run.Id, null, CancellationToken.None));
        Assert.Single(_Source.PostedFindings);

        _Source.ThrowOnPostKey = null;
        var retry = await _Service.PublishAsync(run.Id, null, CancellationToken.None);

        Assert.True(retry.Accepted);
        Assert.Equal(1, retry.PublishedCount);
        Assert.Equal(1, retry.AlreadyPublishedCount);
        Assert.Equal(2, _Source.PostedFindings.Count);
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
        var persisted = Assert.Single(_Store.Runs.Single().Findings, f => f.DedupeKey == "existing-key");
        Assert.True(persisted.Published);
        Assert.Equal(77, persisted.ThreadId);
    }

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