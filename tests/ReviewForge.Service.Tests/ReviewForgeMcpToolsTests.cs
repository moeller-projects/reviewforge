using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Service.Queue;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class ReviewForgeMcpToolsTests
{
    private readonly ReviewQueue _Queue = new();
    private readonly RunTracker _Tracker = new();
    private readonly InFlightClaims _Claims = new();
    private readonly FakeFindingStore _Store = new();
    private readonly FakePullRequestSource _Source = new();

    private ReviewForgeMcpTools CreateTools(IReviewQueue? queue = null)
        => new(new RunSubmissionService(
            queue ?? _Queue, _Tracker, _Claims, _Store, TimeProvider.System,
            NullLogger<RunSubmissionService>.Instance), _Source);

    [Fact]
    public void Enqueue_accepts_and_returns_run_id_and_status_url()
    {
        var result = CreateTools().EnqueueReview("org", "proj", "repo", 42);

        Assert.True(result.Accepted);
        Assert.NotNull(result.RunId);
        Assert.Equal($"/reviews/{result.RunId}", result.StatusUrl);
        Assert.Null(result.Error);
        Assert.Equal(RunState.Queued, _Tracker.Get(result.RunId.Value)!.State);
    }

    [Fact]
    public void Enqueue_conflict_reports_in_flight_holder()
    {
        var tools = CreateTools();
        var first = tools.EnqueueReview("org", "proj", "repo", 42);

        var second = tools.EnqueueReview("org", "proj", "repo", 42);

        Assert.False(second.Accepted);
        Assert.Equal(first.RunId, second.RunId); // the holder's run id is exposed
        Assert.Null(second.StatusUrl);
        Assert.Contains("already in flight", second.Error);
    }

    [Fact]
    public void Enqueue_queue_full_reports_depth_and_capacity()
    {
        var tools = CreateTools(queue: new ReviewQueue(capacity: 1));
        Assert.True(tools.EnqueueReview("org", "proj", "repo", 1).Accepted);

        var rejected = tools.EnqueueReview("org", "proj", "repo", 2);

        Assert.False(rejected.Accepted);
        Assert.Null(rejected.RunId);
        Assert.Contains("queue full", rejected.Error);
        Assert.Contains("1", rejected.Error);
        // A full-queue rejection must roll back: the same PR can be submitted once space frees.
        Assert.True(CreateTools().EnqueueReview("org", "proj", "repo", 2).Accepted);
    }

    [Fact]
    public void Enqueue_invalid_request_reports_validation_errors()
    {
        var result = CreateTools().EnqueueReview("org", "proj", "repo", 0);

        Assert.False(result.Accepted);
        Assert.Contains("invalid request", result.Error);
    }

    [Fact]
    public async Task Status_reads_through_tracker_then_store()
    {
        var tools = CreateTools();
        var queued = tools.EnqueueReview("org", "proj", "repo", 42);

        var status = await tools.GetReviewStatus(queued.RunId!.Value, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(RunState.Queued, status.State);

        // Tracker entry gone (retention expiry / restart): the store row still answers.
        var completedId = Guid.NewGuid();
        _Store.Runs.Add(new ReviewRun(
            completedId, new PrKey("org", "proj", "repo", 7), "sha", ReviewKind.Full,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow, Success: true, [],
            Pipeline: nameof(RunKind.Review)));
        var completed = await tools.GetReviewStatus(completedId, CancellationToken.None);
        Assert.NotNull(completed);
        Assert.Equal(RunState.Completed, completed.State);

        Assert.Null(await tools.GetReviewStatus(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListOpenPrs_returns_all_open_prs_with_selection_fields()
    {
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 1, "Fix null ref", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-b", "repo-2", 7, "Draft: spike", "Bob", true));

        var result = await CreateTools().ListOpenPrs(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.PullRequests.Count);
        var first = result.PullRequests[0];
        Assert.Equal("org", first.Org);
        Assert.Equal("proj-a", first.Project);
        Assert.Equal("repo-1", first.RepositoryId);
        Assert.Equal(1, first.PrId);
        Assert.Equal("Fix null ref", first.Title);
        Assert.Equal("Alice", first.CreatorName);
        Assert.False(first.IsDraft);
        Assert.Equal("feature/fix", first.SourceBranch);
        Assert.Equal("main", first.TargetBranch);
        Assert.True(result.PullRequests[1].IsDraft);
    }

    [Fact]
    public async Task ListOpenPrs_filters_by_project_and_repository_case_insensitively()
    {
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 1, "One", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-b", "repo-2", 2, "Two", "Bob", false));

        var byProject = await CreateTools().ListOpenPrs(project: "PROJ-A", cancellationToken: CancellationToken.None);
        Assert.Single(byProject.PullRequests);
        Assert.Equal(1, byProject.PullRequests[0].PrId);

        var byRepo = await CreateTools().ListOpenPrs(repositoryId: "REPO-2", cancellationToken: CancellationToken.None);
        Assert.Single(byRepo.PullRequests);
        Assert.Equal(2, byRepo.PullRequests[0].PrId);

        var noMatch = await CreateTools().ListOpenPrs(project: "proj-a", repositoryId: "repo-2", cancellationToken: CancellationToken.None);
        Assert.Empty(noMatch.PullRequests);
    }

    [Fact]
    public async Task ListOpenPrs_bounds_results_and_reports_truncation()
    {
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 3, "Three", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 1, "One", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 2, "Two", "Alice", false));

        var bounded = await CreateTools().ListOpenPrs(maxResults: 2, cancellationToken: CancellationToken.None);

        Assert.Equal(3, bounded.TotalCount);
        Assert.True(bounded.Truncated);
        Assert.Equal(2, bounded.PullRequests.Count);
        // Deterministic truncation order: project, repository, then ascending prId (oldest first).
        Assert.Equal(1, bounded.PullRequests[0].PrId);
        Assert.Equal(2, bounded.PullRequests[1].PrId);

        var clamped = await CreateTools().ListOpenPrs(maxResults: 0, cancellationToken: CancellationToken.None);
        Assert.Single(clamped.PullRequests);
    }

    [Fact]
    public async Task ListOpenPrs_offset_pages_to_remaining_results()
    {
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 1, "One", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 2, "Two", "Alice", false));
        _Source.OpenPullRequests.Add(Candidate("org", "proj-a", "repo-1", 3, "Three", "Alice", false));

        var first = await CreateTools().ListOpenPrs(maxResults: 2, cancellationToken: CancellationToken.None);
        var rest = await CreateTools().ListOpenPrs(maxResults: 2, offset: 2, cancellationToken: CancellationToken.None);

        Assert.True(first.Truncated);
        Assert.False(rest.Truncated);
        Assert.Equal(3, rest.TotalCount);
        var remaining = Assert.Single(rest.PullRequests);
        Assert.Equal(3, remaining.PrId);

        var beyond = await CreateTools().ListOpenPrs(offset: 10, cancellationToken: CancellationToken.None);
        Assert.False(beyond.Truncated);
        Assert.Empty(beyond.PullRequests);

        var negative = await CreateTools().ListOpenPrs(offset: -1, cancellationToken: CancellationToken.None);
        Assert.Equal(3, negative.PullRequests.Count);
    }

    private static PullRequestCandidate Candidate(
        string org, string project, string repo, int prId, string title, string creator, bool isDraft)
        => new(new PrKey(org, project, repo, prId),
            new PullRequest(prId, title, null, "sha", "base-sha", "url", isDraft, $"id-{creator}", creator,
                SourceRefName: "refs/heads/feature/fix"),
            "refs/heads/main", $"id-{creator}", creator);
}