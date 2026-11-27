using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public sealed class RunHistoryStoreTests : IDisposable
{
    private readonly string _Path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.db");
    private IFindingStore Store(bool sqlite) => sqlite ? new SqliteFindingStore($"Data Source={_Path};Pooling=False") : new FakeFindingStore();
    private static ReviewRun Run(int day, PrKey? pr = null, string head = "head") => new(Guid.NewGuid(), pr ?? new("o", "p", "r", 1), head,
        ReviewKind.Full, DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(day), DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddDays(day + 1), true, []);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Global_history_is_bounded_chronological_scoped_and_null_is_not_zero(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(sqlite);
        var history = (IRunHistoryStore)store;
        var older = Run(1, head: "zz");
        var newer = Run(2, head: "aa") with { FindingObservations = [] };
        var other = Run(3, new("other", "p", "r", 1));
        foreach (var r in new[] { older, newer, other }) await store.SaveRunAsync(r, ct);
        var page = await history.GetRunHistoryAsync(0, 2, null, false, ct);
        Assert.True(page.HasMore);
        Assert.Equal(new[] { other.Id, newer.Id }, page.Items.Select(r => r.RunId));
        Assert.Null(page.Items[0].Findings);
        Assert.Equal(new FindingObservationCounts(0, 0, 0, 0, 0), page.Items[1].Findings);
        Assert.Equal(older.Id, Assert.Single((await history.GetRunHistoryAsync(2, 2, null, false, ct)).Items).RunId);
        Assert.Equal(new[] { newer.Id, older.Id }, (await history.GetRunHistoryAsync(0, 100, older.Pr, false, ct)).Items.Select(r => r.RunId));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => history.GetRunHistoryAsync(0, 101, null, false, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => history.GetRunHistoryAsync(-1, 1, null, false, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_preserves_tick_precision_and_normalizes_offsets_before_paging(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(sqlite);
        var history = (IRunHistoryStore)store;
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var older = Run(0) with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), StartedAt = at.AddTicks(1) };
        var newer = Run(0) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), StartedAt = at.AddTicks(2).ToOffset(TimeSpan.FromHours(2)) };
        await store.SaveRunAsync(older, ct);
        await store.SaveRunAsync(newer, ct);
        Assert.Equal(newer.Id, Assert.Single((await history.GetRunHistoryAsync(0, 1, null, false, ct)).Items).RunId);
        Assert.Equal(older.Id, Assert.Single((await history.GetRunHistoryAsync(1, 1, null, false, ct)).Items).RunId);
        var sameInstant = newer with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), StartedAt = newer.StartedAt.ToUniversalTime() };
        await store.SaveRunAsync(sameInstant, ct);
        Assert.Equal(new[] { sameInstant.Id, newer.Id, older.Id },
            (await history.GetRunHistoryAsync(0, 100, null, false, ct)).Items.Select(i => i.RunId));
        Assert.Empty((await history.GetRunHistoryAsync(0, 100, null, true, ct)).Items);
        Assert.Equal(newer.Id, Assert.Single((await history.GetRunHistoryAsync(0, 100, null, true, ct, [newer.Id])).Items).RunId);
    }

    [Fact]
    public async Task Legacy_history_sort_keys_are_backfilled_at_full_precision()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(true);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var older = Run(0) with { Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), StartedAt = at.AddTicks(1) };
        var newer = Run(0) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), StartedAt = at.AddTicks(2).ToOffset(TimeSpan.FromHours(-3)) };
        await store.SaveRunAsync(older, ct);
        await store.SaveRunAsync(newer, ct);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_Path};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP INDEX IX_Runs_HistoryTicks; DROP INDEX IX_Runs_PrHistoryTicks; ALTER TABLE Runs DROP COLUMN StartedAtUtcTicks;";
            await command.ExecuteNonQueryAsync(ct);
        }
        var upgraded = (IRunHistoryStore)Store(true);
        Assert.Equal(new[] { newer.Id, older.Id },
            (await upgraded.GetRunHistoryAsync(0, 100, null, false, ct)).Items.Select(i => i.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attention_finds_old_drafts_and_latest_decisions_never_override_technical_rejection(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(sqlite);
        var history = (IRunHistoryStore)store;
        var accepted = new FindingObservation(Guid.NewGuid(), "accepted", "{}", false, FindingValidationStatus.Accepted, "", "", FindingVerificationStatus.Confirmed, "", "");
        var rejected = accepted with { Id = Guid.NewGuid(), DedupeKey = "rejected", Verification = FindingVerificationStatus.Rejected };
        var posted = accepted with { Id = Guid.NewGuid(), DedupeKey = "posted", NewlyPosted = true };
        var draft = Run(0) with { Pipeline = "ReviewDraft", FindingObservations = [accepted, rejected, posted], Findings = [Row("accepted"), Row("rejected"), Row("posted") with { Published = true }, Row("carried"), Row("thread-1")] };
        await store.SaveRunAsync(draft, ct);
        for (var i = 1; i <= 110; i++) await store.SaveRunAsync(Run(i), ct);
        var summary = Assert.Single((await history.GetRunHistoryAsync(0, 1, null, true, ct)).Items);
        Assert.Equal(3, summary.PendingDecisions);
        Assert.Equal(new FindingObservationCounts(3, 2, 1, 0, 1), summary.Findings);
        var decisions = (IFindingDecisionStore)store;
        foreach (var o in new[] { accepted, rejected, posted })
            Assert.True(await decisions.TryAppendFindingDecisionAsync(FindingDecision.Record(draft.Id, o.Id.ToString("N"), draft.HeadSha, FindingDecisionState.Approved, null, "test", DateTimeOffset.UtcNow, 1), 0, ct));
        summary = Assert.Single((await history.GetRunHistoryAsync(0, 1, null, true, ct)).Items);
        Assert.Equal(0, summary.PendingDecisions);
        Assert.Equal(1, summary.ApprovedUnpublished);
        Assert.True(await decisions.TryAppendFindingDecisionAsync(FindingDecision.Record(draft.Id, accepted.Id.ToString("N"), draft.HeadSha, FindingDecisionState.Rejected, "no", "test", DateTimeOffset.UtcNow, 2), 1, ct));
        Assert.Empty((await history.GetRunHistoryAsync(0, 100, null, true, ct)).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_payload_rows_only_and_ties_have_stable_identity_order(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(sqlite);
        var a = Run(1) with { Pipeline = "ReviewDraft", Findings = [Row("full"), Row("identity") with { FindingJson = null }, Row("thread-12")] };
        var b = Run(1) with { Pipeline = "Resolve", HeadSha = "" };
        await store.SaveRunAsync(a, ct);
        await store.SaveRunAsync(b, ct);
        var rows = (await ((IRunHistoryStore)store).GetRunHistoryAsync(0, 100, null, false, ct)).Items;
        Assert.Equal(new[] { a.Id, b.Id }.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal), rows.Select(r => r.RunId));
        Assert.Equal(1, rows.Single(r => r.RunId == a.Id).PendingDecisions);
        Assert.Null(rows.Single(r => r.RunId == b.Id).HeadSha);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_is_explicit_and_deferred_is_not_conflated_with_pending(bool sqlite)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(sqlite);
        var run = Run(1) with { Pipeline = "ReviewDraft", Findings = [Row("key")] };
        await store.SaveRunAsync(run, ct);
        var decisions = (IFindingDecisionStore)store;
        Assert.True(await decisions.TryAppendFindingDecisionAsync(FindingDecision.Record(run.Id, "key", run.HeadSha,
            FindingDecisionState.Deferred, null, "test", DateTimeOffset.UtcNow, 1), 0, ct));
        Assert.Empty((await ((IRunHistoryStore)store).GetRunHistoryAsync(0, 100, null, true, ct)).Items);
        Assert.True(await decisions.TryAppendFindingDecisionAsync(FindingDecision.Record(run.Id, "key", run.HeadSha,
            FindingDecisionState.Pending, null, "test", DateTimeOffset.UtcNow, 2), 1, ct));
        Assert.Equal(1, Assert.Single((await ((IRunHistoryStore)store).GetRunHistoryAsync(0, 100, null, true, ct)).Items).PendingDecisions);
    }

    [Fact]
    public async Task Ordinary_page_only_inspects_selected_observations_and_never_parses_finding_payloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = (SqliteFindingStore)Store(true);
        var old = Run(1);
        await store.SaveRunAsync(old, ct);
        var observed = new FindingObservation(Guid.NewGuid(), "key", "not-json", false, FindingValidationStatus.Accepted, "", "", FindingVerificationStatus.Confirmed, "", "");
        await store.SaveRunAsync(Run(2), ct); // Valid scalar lookahead, older corrupt row is outside the bound.
        var latest = Run(3) with { FindingObservations = [observed], Findings = [Row("key") with { FindingJson = "not-json" }] };
        await store.SaveRunAsync(latest, ct);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_Path};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Runs SET FindingObservationsJson='invalid-old-json' WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", old.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var page = await store.GetRunHistoryAsync(0, 1, null, false, ct);
        Assert.Equal(latest.Id, Assert.Single(page.Items).RunId);
        Assert.Equal(1, page.Items[0].Findings!.Accepted);
        Assert.True(page.HasMore);
    }

    private static StoredFinding Row(string key) => new(key, "rule", "warning", "title", null, null, null, FindingJson: "{}");
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_Path + suffix); }
}
