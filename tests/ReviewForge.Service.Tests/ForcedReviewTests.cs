using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

[Collection("ReviewForge service host")]
public sealed class ForcedReviewTests
{
    [Theory]
    [InlineData(false, false, RunState.Skipped, "Memory")]
    [InlineData(true, false, RunState.Completed, "Memory")]
    [InlineData(true, true, RunState.Skipped, "Memory")]
    [InlineData(false, false, RunState.Skipped, "Sqlite")]
    [InlineData(true, false, RunState.Completed, "Sqlite")]
    [InlineData(true, true, RunState.Skipped, "Sqlite")]
    public async Task Force_bypasses_only_unchanged_head_not_drafts(bool force, bool draft, RunState expected, string queueMode)
    {
        using var environment = new EnvironmentVariableScope();
        environment.Set("Persistence__QueueMode", queueMode);
        using var factory = new ReviewForgeFactory();
        factory.Source.Pr = factory.Source.Pr with { IsDraft = draft };
        factory.Source.ChangedFiles = [new ChangedFile("src/A.cs", ChangedFileType.Edit)];
        factory.Git.Diff = "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var pr = new PrKey("o", "p", "r", 7);
        factory.Store.LastRun = new PriorRun(pr, factory.Source.Pr.SourceCommitSha, DateTimeOffset.UtcNow, []);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/reviews", new
        {
            org = "o", project = "p", repositoryId = "r", prId = 7, forceReview = force,
            headSha = "untrusted-browser-head",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<SubmitReviewResponse>(cancellationToken: TestContext.Current.CancellationToken))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        RunStatus? status;
        do
        {
            await Task.Delay(20, timeout.Token);
            status = await client.GetFromJsonAsync<RunStatus>(accepted.StatusUrl, timeout.Token);
        } while (status!.State is RunState.Queued or RunState.Running);
        Assert.Equal(expected, status.State);
        Assert.Equal(factory.Source.Pr.SourceCommitSha, status.HeadSha);
        Assert.Equal(expected == RunState.Completed ? 1 : 0, factory.Store.Runs.Count);
        Assert.Equal(expected == RunState.Completed, factory.Chat.Calls > 0);
        var claims = factory.Services.GetRequiredService<InFlightClaims>();
        while (claims.IsHeldBy(pr, accepted.RunId)) await Task.Delay(20, timeout.Token);
        var next = factory.Services.GetRequiredService<RunSubmissionService>()
            .Submit(new SubmitReviewRequest("o", "p", "r", 7, ForceReview: true), RunKind.Review);
        Assert.IsType<SubmitOutcome.Accepted>(next);
    }

    [Fact]
    public async Task Concurrent_forced_requests_accept_exactly_one()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var runs = factory.Services.GetRequiredService<RunSubmissionService>();
        var request = new SubmitReviewRequest("o", "p", "r", 7, ForceReview: true);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => runs.Submit(request, RunKind.Review), TestContext.Current.CancellationToken)));
        Assert.Single(results.OfType<SubmitOutcome.Accepted>());
        Assert.Equal(7, results.OfType<SubmitOutcome.Conflict>().Count());
        Assert.Equal(1, factory.Services.GetRequiredService<IReviewQueue>().ApproximateDepth);
    }

    [Fact]
    public void Full_queue_rolls_back_forced_claim()
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var queue = factory.Services.GetRequiredService<IReviewQueue>();
        for (var i = 0; i < queue.Capacity; i++)
            Assert.True(queue.TryEnqueue(new ReviewRequest(Guid.NewGuid(), new PrKey("o", "p", "r", i + 100), DateTimeOffset.UtcNow)).Accepted);
        var runs = factory.Services.GetRequiredService<RunSubmissionService>();
        var request = new SubmitReviewRequest("o", "p", "r", 7, ForceReview: true);
        Assert.IsType<SubmitOutcome.QueueFull>(runs.Submit(request, RunKind.Review));
        var claims = factory.Services.GetRequiredService<InFlightClaims>();
        var owner = Guid.NewGuid();
        Assert.True(claims.TryClaim(new PrKey("o", "p", "r", 7), owner, out _));
        claims.Release(new PrKey("o", "p", "r", 7), owner);
    }

    [Fact]
    public async Task Failed_force_releases_claim_for_next_force()
    {
        using var factory = new ReviewForgeFactory();
        factory.Source.ThrowOnGetPullRequest = new InvalidOperationException("test failure");
        using var client = factory.CreateClient();
        var runs = factory.Services.GetRequiredService<RunSubmissionService>();
        var request = new SubmitReviewRequest("o", "p", "r", 7, ForceReview: true);
        var first = Assert.IsType<SubmitOutcome.Accepted>(runs.Submit(request, RunKind.Review));
        var tracker = factory.Services.GetRequiredService<RunTracker>();
        var claims = factory.Services.GetRequiredService<InFlightClaims>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (tracker.Get(first.RunId)?.State != RunState.Failed ||
               claims.IsHeldBy(new PrKey("o", "p", "r", 7), first.RunId))
            await Task.Delay(20, timeout.Token);
        Assert.IsType<SubmitOutcome.Accepted>(runs.Submit(request, RunKind.Review));
    }

    [Theory]
    [InlineData(RunKind.Review)]
    [InlineData(RunKind.ReviewDraft)]
    [InlineData(RunKind.Resolve)]
    public void Force_conflicts_with_any_competing_run_kind(RunKind kind)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var runs = factory.Services.GetRequiredService<RunSubmissionService>();
        var request = new SubmitReviewRequest("o", "p", "r", 7);
        var holder = Assert.IsType<SubmitOutcome.Accepted>(runs.Submit(request, kind));
        var conflict = Assert.IsType<SubmitOutcome.Conflict>(runs.Submit(request with { ForceReview = true }, RunKind.Review));
        Assert.Equal(holder.RunId, conflict.Holder);
    }

    [Theory]
    [InlineData(RunKind.Resolve)]
    [InlineData(RunKind.ReviewDraft)]
    public void Shared_submission_rejects_forced_non_review_before_claiming(RunKind kind)
    {
        using var factory = new ReviewForgeFactory().WithoutWorkers();
        using var client = factory.CreateClient();
        var runs = factory.Services.GetRequiredService<RunSubmissionService>();
        var request = System.Text.Json.JsonSerializer.Deserialize<SubmitReviewRequest>(
            """{"Org":"o","Project":"p","RepositoryId":"r","PrId":7,"ForceReview":true}""")!;
        Assert.IsType<SubmitOutcome.Invalid>(runs.Submit(request, kind));
        Assert.IsType<SubmitOutcome.Accepted>(runs.Submit(request, RunKind.Review));
    }
}
