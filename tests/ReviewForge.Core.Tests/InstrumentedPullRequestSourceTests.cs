using System.Diagnostics.Metrics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Pipeline;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class InstrumentedPullRequestSourceTests
{
    private static readonly PrKey Pr = new("org", "proj", "repo", 7);

    [Fact]
    public async Task Delegates_all_methods_and_returns_inner_results()
    {
        var inner = new FakePullRequestSource
        {
            OpenPullRequests =
            [
                new PullRequestCandidate(
                    Pr, new PullRequest(7, "t", "d", "head", "base", "url", IsDraft: false),
                    TargetBranch: "main", CreatorId: "alice", CreatorName: "Alice"),
            ],
            WorkItems = [new WorkItem(1, "wi", "Task", "desc", "ac", "New")],
            ChangedFiles = [new ChangedFile("a.cs", ChangedFileType.Add)],
            Threads =
            [
                new ReviewThread(1, "rule-1", ReviewThreadStatus.Active,
                    [new ThreadComment("author", "Author", IsBot: false, "body", DateTimeOffset.UtcNow)]),
            ],
        };
        var source = new InstrumentedPullRequestSource(inner);
        var finding = new RichFinding
        {
            RuleId = "rule-1",
            Title = "t",
            Severity = "medium",
            Category = "bug",
            Description = "msg",
            Anchor = new FindingAnchor("a.cs", 1, 1),
        };

        Assert.Equal(inner.Pr, await source.GetPullRequestAsync(Pr, CancellationToken.None));
        Assert.Single(await source.GetOpenPullRequestsAsync(CancellationToken.None));
        Assert.Single(await source.GetLinkedWorkItemsAsync(Pr, CancellationToken.None));
        Assert.Single(await source.GetChangedFilesAsync(Pr, CancellationToken.None));
        Assert.Single(await source.GetThreadsAsync(Pr, CancellationToken.None));
        Assert.Equal(inner.User, await source.GetCurrentUserAsync(CancellationToken.None));

        Assert.Equal(1000, await source.PostFindingThreadAsync(Pr, finding, CancellationToken.None));
        await source.PostGeneralCommentAsync(Pr, "comment", dedupeKey: null, ct: CancellationToken.None);
        await source.ReplyToThreadAsync(Pr, 1, "reply", CancellationToken.None);
        await source.SetThreadStatusAsync(Pr, 1, ReviewThreadStatus.Fixed, CancellationToken.None);
        await source.SetReviewerVoteAsync(Pr, "user-1", ReviewerVote.Approved, CancellationToken.None);

        Assert.Single(inner.PostedFindings);
        Assert.Single(inner.GeneralComments);
        Assert.Single(inner.Replies);
        Assert.Single(inner.StatusChanges);
        Assert.Single(inner.Votes);
    }

    [Fact]
    public async Task Propagates_failures_without_wrapping()
    {
        var source = new InstrumentedPullRequestSource(new ExplosiveSource());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetPullRequestAsync(Pr, CancellationToken.None));
    }

    [Fact]
    public async Task Propagates_cancellation_as_cancellation()
    {
        var source = new InstrumentedPullRequestSource(new CancellingSource());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetOpenPullRequestsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_and_cancelled_calls_still_record_duration()
    {
        var measurements = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reviewforge.ado.call_duration_ms")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            Interlocked.Increment(ref measurements));
        listener.Start();

        var explosive = new InstrumentedPullRequestSource(new ExplosiveSource());
        var cancelling = new InstrumentedPullRequestSource(new CancellingSource());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => explosive.GetPullRequestAsync(Pr, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => explosive.PostGeneralCommentAsync(
                Pr, "comment", dedupeKey: null, ct: CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cancelling.GetOpenPullRequestsAsync(CancellationToken.None));

        Assert.Equal(3, measurements);
    }

    private sealed class ExplosiveSource : FakePullRequestSource
    {
        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
            => throw new InvalidOperationException("boom");

        public override Task PostGeneralCommentAsync(
            PrKey pr,
            string text,
            string? dedupeKey,
            CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CancellingSource : FakePullRequestSource
    {
        public override Task<IReadOnlyList<PullRequestCandidate>> GetOpenPullRequestsAsync(CancellationToken ct)
            => throw new OperationCanceledException("cancelled");
    }
}
