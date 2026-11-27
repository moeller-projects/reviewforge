using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class FindingPublicationAuditTests
{
    private static RichFinding Finding(string key, bool inline = true) => new()
    {
        RuleId = "rule", Title = key, Severity = "high", Category = "bug", Description = "description",
        DedupeKey = key, Anchor = inline ? new FindingAnchor("file.cs", 1, 1) : null,
    };

    private static ReviewContext Context(FakePullRequestSource source, params RichFinding[] findings) => new(
        new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
    {
        Fetch = new() {PullRequest = source.Pr, CurrentUser = source.User, Threads = source.Threads.ToArray()},
        Validation = new() {AcceptedFindings = findings},
        Reasoning = new() {Result = new ReviewResult {Narrative = new(), Findings = findings, Uncertainties = []}},
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Successful_post_is_published_with_returned_thread_id_and_survives_finalize(bool inline)
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var ctx = Context(source, Finding("key", inline));
        await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        await new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        var row = Assert.Single((await store.GetRunAsync(ctx.RunId, CancellationToken.None))!.Findings);
        Assert.True(row.Published);
        Assert.NotNull(row.FindingJson);
        Assert.Contains("\"description\":\"description\"", row.FindingJson);
        Assert.Null(row.AppliedFixJson);
        Assert.Equal(1000, row.ThreadId);
        Assert.Equal(1000, ctx.Published.PostedThreadIds["key"]);
        await store.MarkFindingPublishedAsync(ctx.RunId, "key", null, CancellationToken.None);
        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(row, Assert.Single((await store.GetRunAsync(ctx.RunId, CancellationToken.None))!.Findings));
        var recent = Assert.Single(await store.GetRecentRunsAsync(ctx.Pr, 1, CancellationToken.None));
        Assert.Equal(row, Assert.Single(recent.Findings));
        Assert.Equal(1, FindingObservationCounts.From(recent.FindingObservations!).NewlyPosted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Later_post_failure_retains_successful_publication_only(bool inline)
    {
        var source = new FakePullRequestSource {ThrowOnPostKey = "fails"};
        var store = new FakeFindingStore();
        var ctx = Context(source, Finding("posted", inline), Finding("fails"));
        await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));

        var rows = (await store.GetRunAsync(ctx.RunId, CancellationToken.None))!.Findings;
        Assert.True(rows.Single(f => f.DedupeKey == "posted").Published);
        Assert.Equal(1000, rows.Single(f => f.DedupeKey == "posted").ThreadId);
        Assert.False(rows.Single(f => f.DedupeKey == "fails").Published);
        Assert.Null(rows.Single(f => f.DedupeKey == "fails").ThreadId);
        Assert.Empty(source.Votes); // The failed publication never reaches the summary/vote tail.
        Assert.Equal(1, FindingObservationCounts.From(ctx.SnapshotFindingObservations()).NewlyPosted);
    }

    [Theory]
    [InlineData(ReviewThreadStatus.Active)]
    [InlineData(ReviewThreadStatus.Pending)]
    public async Task Legacy_live_thread_reconciles_publication_on_retry_without_reposting(ReviewThreadStatus status)
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(17, "key", status, []));
        var store = new FakeFindingStore();
        var ctx = Context(source, Finding("key", false));
        await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
        var stage = new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance);

        await stage.ExecuteAsync(ctx, CancellationToken.None);
        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.PostedFindings);
        Assert.DoesNotContain("key", source.GeneralCommentDedupeKeys);
        var row = Assert.Single((await store.GetRunAsync(ctx.RunId, CancellationToken.None))!.Findings);
        Assert.True(row.Published);
        Assert.Equal(17, row.ThreadId);
        Assert.Equal(17, ctx.Published.PostedThreadIds["key"]);
        Assert.Equal(0, FindingObservationCounts.From(ctx.SnapshotFindingObservations()).NewlyPosted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Regression_is_reconciled_only_after_thread_is_actually_reopened(bool reopened)
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(17, "key", ReviewThreadStatus.Fixed, []));
        var store = new FakeFindingStore();
        var ctx = Context(source, Finding("key"));
        ctx.Reasoning.Collector.MarkRegressed("key");
        await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
        // ctx.Fetch remains the pre-triage snapshot; the source reflects the actual outcome.
        if (reopened)
            source.Threads[0] = source.Threads[0] with {Status = ReviewThreadStatus.Active};

        await new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.PostedFindings);
        var row = Assert.Single((await store.GetRunAsync(ctx.RunId, CancellationToken.None))!.Findings);
        Assert.Equal(reopened, row.Published);
        Assert.Equal(reopened ? 17 : (int?)null, row.ThreadId);
        Assert.Equal(0, FindingObservationCounts.From(ctx.SnapshotFindingObservations()).NewlyPosted);
    }
}
