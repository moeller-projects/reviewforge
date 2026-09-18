using Microsoft.EntityFrameworkCore;
using ReviewForge.Core.Domain;
using ReviewForge.Infrastructure.Persistence;
using Xunit;

namespace ReviewForge.Infrastructure.Tests;

public class SqliteFindingStoreTests : IDisposable
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);
    private readonly string _ConnectionString;
    private readonly string _DbPath;
    private readonly SqliteFindingStore _Store;

    public SqliteFindingStoreTests()
    {
        _DbPath = Path.Combine(Path.GetTempPath(), "reviewforge-store-" + Guid.NewGuid().ToString("N") + ".db");
        _ConnectionString = $"Data Source={_DbPath};Pooling=False";
        _Store = new SqliteFindingStore(_ConnectionString);
    }

    public void Dispose()
    {
        if (File.Exists(_DbPath))
        {
            File.Delete(_DbPath);
        }
    }

    private static ReviewRun Run(string head, DateTimeOffset completed, bool success = true, params string[] keys)
        => new(Guid.NewGuid(), Key, head, ReviewKind.Full, completed.AddMinutes(-5), completed, success,
            [.. keys.Select(k => new StoredFinding(k, "rule", "high", "title", "f.cs", 1, null))]);

    [Fact]
    public async Task Empty_store_returns_null_and_no_keys()
    {
        Assert.Null(await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None));
        Assert.Empty(await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task Save_and_read_back_latest_completed_run()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(Run("old-head", t0, keys: ["k1"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("new-head", t0.AddHours(1), keys: ["k2"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("failed-head", t0.AddHours(2), success: false), CancellationToken.None);

        var last = await _Store.GetLastCompletedRunAsync(Key, CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal("new-head", last.HeadSha); // failed run ignored
        Assert.Equal(["k2"], last.FindingKeys);
    }

    [Fact]
    public async Task Known_keys_are_distinct_across_runs_and_scoped_to_pr()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        await _Store.SaveRunAsync(Run("h1", t0, keys: ["k1", "k2"]), CancellationToken.None);
        await _Store.SaveRunAsync(Run("h2", t0.AddHours(1), keys: ["k2", "k3"]), CancellationToken.None);

        var otherPr = Key with {PrId = 99};
        await _Store.SaveRunAsync(
            new ReviewRun(Guid.NewGuid(), otherPr, "hx", ReviewKind.Full, t0, t0, true,
                [new StoredFinding("other", "r", "s", "t", null, null, null)]),
            CancellationToken.None);

        var keys = await _Store.GetKnownDedupeKeysAsync(Key, CancellationToken.None);
        Assert.Equal(["k1", "k2", "k3"], keys.Order());

        var otherKeys = await _Store.GetKnownDedupeKeysAsync(otherPr, CancellationToken.None);
        Assert.Equal(["other"], otherKeys);
    }

    [Fact]
    public async Task SetThreadId_backfills_finding()
    {
        var t0 = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var run = Run("h", t0, keys: ["k1"]);
        await _Store.SaveRunAsync(run, CancellationToken.None);

        await _Store.SetThreadIdAsync(run.Id, "k1", 1234, CancellationToken.None);

        // Read back through a fresh context to prove persistence.
        await using (var db = new FindingStoreDbContext(
                         new DbContextOptionsBuilder<FindingStoreDbContext>()
                             .UseSqlite(_ConnectionString).Options))
        {
            var entity = Assert.Single(db.Findings);
            Assert.Equal(1234, entity.ThreadId);
            Assert.Equal("f.cs", entity.FilePath);
            Assert.Equal(1, entity.Line);
            Assert.Equal("rule", entity.RuleId);
            Assert.Equal("high", entity.Severity);
            Assert.Equal("title", entity.Title);
            Assert.Equal(run.Id, entity.RunId);
            Assert.True(entity.Id > 0);
        }

        var verify = new SqliteFindingStore(_ConnectionString);
        var last = await verify.GetLastCompletedRunAsync(Key, CancellationToken.None);
        Assert.Equal(["k1"], last!.FindingKeys);
    }
}