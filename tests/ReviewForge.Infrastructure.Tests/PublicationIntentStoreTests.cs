using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class PublicationIntentStoreTests : IDisposable
{
    private readonly string _Path = Path.Combine(Path.GetTempPath(), $"reviewforge-publication-intent-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_Path};Pooling=False";
    private static readonly PrKey Key = new("org", "project", "repo", 1);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Intent_roundtrips_without_inventing_observations_and_survives_stale_finalization(bool sqlite, bool captured)
    {
        var ct = TestContext.Current.CancellationToken;
        IFindingStore store = sqlite ? new SqliteFindingStore(ConnectionString) : new FakeFindingStore();
        var finding = new RichFinding { RuleId = "r", Title = "finding", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, [new("key", "r", "high", "finding", null, null, null)], Pipeline: "Review",
            FindingObservations: captured ? [FindingObservation.Create(finding)] : null);
        await store.SaveRunAsync(run, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetFindingPublicationAttemptedAsync(run.Id, "missing", true, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetFindingPublicationAttemptedAsync(Guid.NewGuid(), "key", true, ct));
        await store.SetFindingPublicationAttemptedAsync(run.Id, "key", true, ct);
        await store.SaveRunAsync(run with { FindingObservations = null }, ct);
        if (sqlite) store = new SqliteFindingStore(ConnectionString);
        var saved = (await store.GetRunAsync(run.Id, ct))!;
        Assert.True(Assert.Single(saved.Findings).PublicationAttempted);
        Assert.True(Assert.Single((await store.GetRecentRunsAsync(Key, 1, ct))[0].Findings).PublicationAttempted);
        if (captured) Assert.True(Assert.Single(saved.FindingObservations!).PublicationAttempted);
        else Assert.Null(saved.FindingObservations);
        await store.SetFindingPublicationAttemptedAsync(run.Id, "key", false, ct);
        saved = (await store.GetRunAsync(run.Id, ct))!;
        Assert.False(Assert.Single(saved.Findings).PublicationAttempted);
        if (captured) Assert.False(Assert.Single(saved.FindingObservations!).PublicationAttempted);
        else Assert.Null(saved.FindingObservations);
        await store.SetFindingPublicationAttemptedAsync(run.Id, "key", true, ct);
        await store.RecordFindingPublicationAsync(run.Id, "key", 42, true, ct);
        saved = (await store.GetRunAsync(run.Id, ct))!;
        Assert.True(Assert.Single(saved.Findings).Published);
        Assert.Equal(42, saved.Findings[0].ThreadId);
        if (sqlite) Assert.True(Assert.Single((await store.GetLastCompletedRunAsync(Key, ct))!.Findings!).PublicationAttempted);
    }

    [Fact]
    public async Task Intent_and_observation_updates_rollback_together()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteFindingStore(ConnectionString);
        var finding = new RichFinding { RuleId = "r", Title = "finding", Severity = "high", Category = "bug", Description = "claim", DedupeKey = "key" };
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, [new("key", "r", "high", "finding", null, null, null)], FindingObservations: [FindingObservation.Create(finding)]);
        await store.SaveRunAsync(run, ct);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailIntentAudit BEFORE UPDATE OF FindingObservationsJson ON Runs BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;";
            await command.ExecuteNonQueryAsync(ct);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => store.SetFindingPublicationAttemptedAsync(run.Id, "key", true, ct));
        var saved = (await store.GetRunAsync(run.Id, ct))!;
        Assert.False(Assert.Single(saved.Findings).PublicationAttempted);
        Assert.False(Assert.Single(saved.FindingObservations!).PublicationAttempted);
    }

    [Fact]
    public async Task Upgrade_adds_intent_to_existing_findings_without_reconstructing_legacy_observations()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteFindingStore(ConnectionString);
        var run = new ReviewRun(Guid.NewGuid(), Key, "head", ReviewKind.Full, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            true, [new("key", "r", "high", "finding", null, null, null)]);
        await store.SaveRunAsync(run, ct);
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE Findings DROP COLUMN PublicationAttempted";
            await command.ExecuteNonQueryAsync(ct);
        }
        store = new SqliteFindingStore(ConnectionString);
        var saved = (await store.GetRunAsync(run.Id, ct))!;
        Assert.False(Assert.Single(saved.Findings).PublicationAttempted);
        Assert.Null(saved.FindingObservations);
        await store.SetFindingPublicationAttemptedAsync(run.Id, "key", true, ct);
        store = new SqliteFindingStore(ConnectionString);
        Assert.True(Assert.Single((await store.GetRunAsync(run.Id, ct))!.Findings).PublicationAttempted);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_Path + suffix);
    }
}
