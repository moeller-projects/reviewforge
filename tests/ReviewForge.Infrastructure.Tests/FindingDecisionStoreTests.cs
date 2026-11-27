using Microsoft.Data.Sqlite;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class FindingDecisionStoreTests : IDisposable
{
    private readonly string _Path = Path.Combine(Path.GetTempPath(), $"decisions-{Guid.NewGuid():N}.db");
    private string Connection => $"Data Source={_Path};Pooling=False";
    private static ReviewRun Run() => new(Guid.NewGuid(), new("o", "p", "r", 1), "head", ReviewKind.Full,
        DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1), true, []);
    private static FindingDecision Decision(ReviewRun run, long version, FindingDecisionState state = FindingDecisionState.Approved)
        => FindingDecision.Record(run.Id, "finding", run.HeadSha, state,
            state == FindingDecisionState.Rejected ? "Not actionable" : null, "dashboard-session", DateTimeOffset.UtcNow, version);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Immutable_history_latest_and_concurrency_have_adapter_parity(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        IFindingStore store = sqlite ? new SqliteFindingStore(Connection) : new FakeFindingStore();
        var decisions = (IFindingDecisionStore)store;
        var run = Run();
        await store.SaveRunAsync(run, ct);
        Assert.Empty(await decisions.GetFindingDecisionsAsync(run.Id, ct));
        var first = Decision(run, 1);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var writer = sqlite ? new SqliteFindingStore(Connection) : decisions;
            return await writer.TryAppendFindingDecisionAsync(first, 0, ct);
        }, ct)));
        Assert.Single(results, x => x);
        Assert.True(await decisions.TryAppendFindingDecisionAsync(Decision(run, 2, FindingDecisionState.Rejected), 1, ct));
        Assert.False(await decisions.TryAppendFindingDecisionAsync(first, 0, ct));
        var updates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            IFindingDecisionStore writer = sqlite ? new SqliteFindingStore(Connection) : decisions;
            return await writer.TryAppendFindingDecisionAsync(Decision(run, 3, FindingDecisionState.Deferred), 2, ct);
        }, ct)));
        Assert.Single(updates, x => x);
        if (sqlite) decisions = new SqliteFindingStore(Connection);
        var latest = Assert.Single(await decisions.GetFindingDecisionsAsync(run.Id, ct));
        Assert.Equal(3, latest.Version);
        var rejected = (await decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 2, ct))[1];
        Assert.Equal("Not actionable", rejected.Reason);
        Assert.Equal("dashboard-session", latest.Actor);
        Assert.Equal(new long[] { 3, 2, 1 }, (await decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 100, ct)).Select(d => d.Version));
        Assert.Single(await decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 1, ct));
        Assert.Empty(await decisions.GetFindingDecisionsAsync(Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 0, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 101, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => decisions.TryAppendFindingDecisionAsync(first, 1, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => decisions.TryAppendFindingDecisionAsync(Decision(run with { HeadSha = "other" }, 3), 2, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => decisions.TryAppendFindingDecisionAsync(Decision(run with { Id = Guid.NewGuid() }, 1), 0, ct));
        await store.SaveRunAsync(run with { Id = Guid.NewGuid(), HeadSha = "next" }, ct);
        Assert.Equal(1, await store.PruneAsync(DateTimeOffset.UtcNow, 0, ct));
        Assert.Empty(await decisions.GetFindingDecisionHistoryAsync(run.Id, "finding", 100, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Updating_an_older_run_does_not_change_prune_retention_order(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        IFindingStore store = sqlite ? new SqliteFindingStore(Connection) : new FakeFindingStore();
        var decisions = (IFindingDecisionStore)store;
        var older = Run();
        var newer = Run();
        await store.SaveRunAsync(older, ct);
        await store.SaveRunAsync(newer, ct);
        await decisions.TryAppendFindingDecisionAsync(Decision(older, 1), 0, ct);
        await store.SaveRunAsync(older with { CompletedAt = DateTimeOffset.UtcNow }, ct);

        Assert.Equal(1, await store.PruneAsync(DateTimeOffset.UtcNow, 1, ct));

        Assert.Null(await store.GetRunAsync(older.Id, ct));
        Assert.NotNull(await store.GetRunAsync(newer.Id, ct));
        Assert.Empty(await decisions.GetFindingDecisionHistoryAsync(older.Id, "finding", 100, ct));
    }

    [Fact]
    public async Task Legacy_database_upgrade_is_additive_and_repeatable()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = new SqliteFindingStore(Connection);
        var run = Run();
        await original.SaveRunAsync(run, ct);
        await using (var connection = new SqliteConnection(Connection))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE FindingDecisions";
            await command.ExecuteNonQueryAsync(ct);
        }
        var upgraded = new SqliteFindingStore(Connection);
        Assert.NotNull(await upgraded.GetRunAsync(run.Id, ct));
        Assert.True(await upgraded.TryAppendFindingDecisionAsync(Decision(run, 1), 0, ct));
        Assert.Single(await new SqliteFindingStore(Connection).GetFindingDecisionsAsync(run.Id, ct));
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_Path + suffix);
    }
}
