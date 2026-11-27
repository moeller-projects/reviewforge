using ReviewForge.Core.Domain;
using ReviewForge.Infrastructure.Persistence;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class RunSummaryOrderingTests : IDisposable
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _DbPath = Path.Combine(Path.GetTempPath(), $"reviewforge-summary-order-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Current_head_attempt_uses_ticks_even_when_PR_latest_is_another_head()
    {
        var older = Run(0, "head");
        var latestAttempt = Run(1, "head");
        var latestPr = Run(2, "another-head");

        await AssertSelectionAsync([older, latestAttempt, latestPr], [latestAttempt.Id, latestPr.Id]);
    }

    [Fact]
    public async Task Current_head_success_uses_ticks_behind_a_newer_failed_attempt()
    {
        var older = Run(0, "head") with { Success = true };
        var latestSuccess = Run(1, "head") with { Success = true };
        var latestAttempt = Run(2, "head");

        await AssertSelectionAsync([older, latestSuccess, latestAttempt], [latestSuccess.Id, latestAttempt.Id]);
    }

    [Fact]
    public async Task PR_latest_uses_ticks_across_heads_without_a_current_head_attempt()
    {
        var older = Run(0, "previous-head");
        var middle = Run(1, "another-head");
        var latest = Run(2, "latest-head");

        await AssertSelectionAsync([older, middle, latest], [latest.Id]);
    }

    [Fact]
    public async Task Latest_incomplete_uses_ticks_across_heads_and_non_review_pipelines()
    {
        var older = Run(0, "previous-head") with { Pipeline = "Resolve", CompletedAt = null };
        var middle = Run(1, "another-head") with { Pipeline = "Resolve", CompletedAt = null };
        var latest = Run(2, "latest-head") with { Pipeline = "Resolve", CompletedAt = null };

        await AssertSelectionAsync([older, middle, latest], [latest.Id]);
    }

    private async Task AssertSelectionAsync(ReviewRun[] runs, Guid[] expectedSummaryIds)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteFindingStore($"Data Source={_DbPath};Pooling=False");
        // Insert against chronology too, so neither GUID nor insertion order can mask lost ticks.
        foreach (var run in runs.Reverse())
            await store.SaveRunAsync(run, ct);

        var reopened = new SqliteFindingStore($"Data Source={_DbPath};Pooling=False");
        var summaries = await reopened.GetRunSummariesAsync([new(Key, "head")], [], [], ct);
        Assert.Equal(expectedSummaryIds.OrderBy(id => id), summaries.Select(s => s.RunId).OrderBy(id => id));

        var expectedHistoryIds = runs.Reverse().Select(r => r.Id).ToArray();
        var history = await reopened.GetRunHistoryAsync(0, 100, Key, false, ct);
        Assert.Equal(expectedHistoryIds, history.Items.Select(r => r.RunId));
        for (var offset = 0; offset < runs.Length; offset++)
        {
            var page = await reopened.GetRunHistoryAsync(offset, 1, Key, false, ct);
            Assert.Equal(expectedHistoryIds[offset], Assert.Single(page.Items).RunId);
        }
    }

    private static ReviewRun Run(int tick, string head)
        => new(Guid.Parse($"00000000-0000-0000-0000-{3 - tick:D12}"), Key, head, ReviewKind.Full,
            Start.AddTicks(tick), Start.AddSeconds(1), false, []);

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_DbPath + suffix);
    }
}
