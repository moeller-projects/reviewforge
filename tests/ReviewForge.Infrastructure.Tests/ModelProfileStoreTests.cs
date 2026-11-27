using Xunit;
using ReviewForge.Core.Profiling;
using ReviewForge.Core.Ports;
using ReviewForge.Infrastructure.Persistence;

namespace ReviewForge.Infrastructure.Tests;

public class ModelProfileStoreTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, "truncated")]
    [InlineData(10, "available")]
    public async Task Stage_usage_and_provider_incomplete_outcomes_survive_restart_expiry_and_legacy_summary_reads(int limit, string expectedState)
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-stages-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var store = new SqliteFindingStore(connectionString);
            var collector = new ModelProfileCollector(Guid.NewGuid(), limit);
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed",
                new() { InputTokenCount = 5, OutputTokenCount = 2, TotalTokenCount = 7, CachedInputTokenCount = 1, ReasoningTokenCount = -1 });
            collector.Begin("VerifyFindings", ModelCallProfile.Verifier, "openai", "gpt-5").Complete("incomplete", null);
            var snapshot = collector.Snapshot();
            var now = DateTimeOffset.UtcNow;
            await store.FinalizeModelProfileAsync(snapshot, now, now.AddDays(1), false, ct);
            store = new SqliteFindingStore(connectionString);
            var page = await store.ReadModelProfileAsync(snapshot.RunId, 0, 100, now, ct);
            Assert.Equal(expectedState, page.State);
            Assert.False(page.Incomplete);
            Assert.True(page.Finalized);
            Assert.Equal(1, page.Aggregate!.ProviderIncompleteCalls);
            var stages = page.Aggregate.Stages!;
            Assert.Equal(1, stages["VerifyFindings"].ProviderIncompleteCalls);
            Assert.Equal("unavailable", stages["VerifyFindings"].Usage!.InputTokens.Coverage);
            Assert.Equal("complete", stages["ExecuteReasoning"].Usage!.InputTokens.Coverage);
            Assert.Equal(5, stages["ExecuteReasoning"].Usage!.InputTokens.Tokens);
            Assert.Equal(1, page.Aggregate.Usage!.CachedInputTokens.Tokens);
            Assert.Equal("partial", page.Aggregate.Usage.InputTokens.Coverage);
            Assert.Null(page.Aggregate.Usage.ReasoningTokens.Tokens);
            var summary = System.Text.Json.JsonSerializer.Serialize(page.Aggregate);
            Assert.Equal(1, await store.ExpireModelProfilesAsync(now.AddDays(1), 1, ct));
            page = await store.ReadModelProfileAsync(snapshot.RunId, 0, 100, now.AddDays(1), ct);
            Assert.Equal("expired", page.State);
            Assert.True(page.Finalized);
            Assert.Empty(page.Calls);
            Assert.Equal(summary, System.Text.Json.JsonSerializer.Serialize(page.Aggregate));
            // Original T10's run-only summary JSON remains readable. Do not infer missing stage/usage facts.
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ModelProfiles SET SummaryJson=$summary WHERE RunId=$run";
            command.Parameters.AddWithValue("$run", snapshot.RunId.ToString("D"));
            command.Parameters.AddWithValue("$summary", "{\"CompletedCalls\":1,\"FailedCalls\":0,\"CancelledCalls\":0,\"InterruptedCalls\":0,\"ObservedDurationMilliseconds\":12}");
            await command.ExecuteNonQueryAsync(ct);
            page = await new SqliteFindingStore(connectionString).ReadModelProfileAsync(snapshot.RunId, 0, 100, now.AddDays(1), ct);
            Assert.Equal(1, page.Aggregate!.CompletedCalls);
            Assert.Null(page.Aggregate.Stages);
            Assert.Null(page.Aggregate.Usage);
            Assert.Null(page.Aggregate.ProviderIncompleteCalls);
            Assert.Null(page.Aggregate.ObservedCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task T10_schema_and_legacy_call_json_upgrade_without_backfilling_activity_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-t10-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var collector = new ModelProfileCollector(Guid.NewGuid());
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            var snapshot = collector.Snapshot();
            var now = DateTimeOffset.UtcNow;
            await new SqliteFindingStore(cs).FinalizeModelProfileAsync(snapshot, now, now.AddDays(30), false, ct);
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(snapshot.Calls[0]))!.AsObject();
            legacy.Remove("LogicalCallId");
            legacy.Remove("AttemptId");
            legacy["SchemaVersion"] = 1;
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(cs))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE ExecutionActivities; UPDATE ModelCalls SET MetadataJson=$json; UPDATE ModelProfiles SET SchemaVersion=1, SummaryJson=$summary";
                command.Parameters.AddWithValue("$json", legacy.ToJsonString());
                command.Parameters.AddWithValue("$summary", "{\"CompletedCalls\":1,\"FailedCalls\":0,\"CancelledCalls\":0,\"InterruptedCalls\":0,\"ObservedDurationMilliseconds\":1}");
                await command.ExecuteNonQueryAsync(ct);
            }
            var page = await new SqliteFindingStore(cs).ReadModelProfileAsync(snapshot.RunId, 0, 100, now, ct);
            Assert.Equal(1, page.SchemaVersion);
            Assert.Empty(page.Activities!);
            Assert.Null(page.Aggregate!.Activities);
            var call = Assert.Single(page.Calls);
            Assert.Equal(1, call.SchemaVersion);
            Assert.Null(call.LogicalCallId);
            Assert.Null(call.AttemptId);
            Assert.Null(call.AttemptNumber);
            Assert.Equal("sdk-internal-unavailable", call.RetryCoverage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Interrupted_activity_beyond_cap_remains_incomplete_after_expiry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-interrupted-tool-{Guid.NewGuid():N}.db");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var collector = new ModelProfileCollector(Guid.NewGuid(), 0);
            using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
            collector.BeginActivity("tool", "EditFile");
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteFindingStore($"Data Source={path};Pooling=False");
            await store.FinalizeModelProfileAsync(collector.Snapshot(), now, now.AddDays(30), false, ct);
            var page = await store.ReadModelProfileAsync(collector.Snapshot().RunId, 0, 100, now, ct);
            Assert.True(page.Incomplete);
            Assert.True(page.Truncated);
            Assert.Equal(0, page.ObservedCalls);
            Assert.Equal(1, page.Aggregate!.Activities!["tool"].Interrupted);
            page = await store.ReadModelProfileAsync(page.RunId, 0, 100, now.AddDays(30), ct);
            Assert.True(page.Incomplete);
            Assert.Equal("expired", page.State);
            Assert.Equal(1, page.Aggregate!.Stages!["ApplyFixes"].Activities!["tool"].Interrupted);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Mixed_detail_rows_share_paging_retention_and_keep_separate_counts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-mixed-{Guid.NewGuid():N}.db");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var collector = new ModelProfileCollector(Guid.NewGuid(), 3);
            using var scope = ModelCallContext.Enter(collector, "ApplyFixes", ModelCallProfile.Fix);
            collector.BeginActivity("tool", "EditFile").Complete("completed");
            collector.Begin("ApplyFixes", ModelCallProfile.Fix, "openai", "gpt-5").Complete("completed", new() { InputTokenCount = 3 });
            collector.BeginActivity("attempt").Complete("failed");
            collector.BeginActivity("tool", "secret-path").Complete("cancelled");
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteFindingStore($"Data Source={path};Pooling=False");
            await store.FinalizeModelProfileAsync(collector.Snapshot(), now, now.AddDays(30), false, ct);
            var page = await store.ReadModelProfileAsync(collector.Snapshot().RunId, 0, 1, now, ct);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(page);
            Assert.True(json.TryGetProperty("Activities", out var rows), "T11 mixed metadata must survive SQLite");
            Assert.Equal(1, rows.GetArrayLength());
            Assert.Empty(page.Calls);
            Assert.Equal(1, page.NextSequence);
            Assert.Equal(1, page.ObservedCalls);
            Assert.Equal(2, page.Aggregate!.Activities!["tool"].Observed);
            page = await store.ReadModelProfileAsync(page.RunId, 1, 1, now, ct);
            Assert.Equal(3, Assert.Single(page.Calls).InputTokens);
            Assert.Equal(2, page.NextSequence);
            page = await store.ReadModelProfileAsync(page.RunId, 2, 1, now, ct);
            Assert.Null(page.NextSequence);
            Assert.Equal("failed", System.Text.Json.JsonSerializer.SerializeToElement(page).GetProperty("Activities")[0].GetProperty("Outcome").GetString());
            Assert.Equal(1, await store.ExpireModelProfilesAsync(now.AddDays(30), 1, ct));
            page = await new SqliteFindingStore($"Data Source={path};Pooling=False").ReadModelProfileAsync(page.RunId, 0, 10, now.AddDays(30), ct);
            Assert.Equal("expired", page.State);
            Assert.Empty(page.Calls);
            Assert.Equal(0, System.Text.Json.JsonSerializer.SerializeToElement(page).GetProperty("Activities").GetArrayLength());
            Assert.Equal(2, page.Aggregate!.Activities!["tool"].Observed);
            Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(page));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Maximum_detail_fixture_has_bounded_pages_and_measured_persistence_overhead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-cap-{Guid.NewGuid():N}.db");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var store = new SqliteFindingStore($"Data Source={path};Pooling=False");
            var collector = new ModelProfileCollector(Guid.NewGuid());
            for (var i = 0; i < 10_001; i++)
                collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            var snapshot = collector.Snapshot();
            var now = DateTimeOffset.UtcNow;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await store.FinalizeModelProfileAsync(snapshot, now, now.AddDays(30), false, ct);
            var write = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            var page = await store.ReadModelProfileAsync(snapshot.RunId, 0, 100, now, ct);
            Assert.Equal(100, page.Calls.Count);
            Assert.Equal(100, page.NextSequence);
            Assert.Equal(10_001, page.ObservedCalls);
            Assert.True(page.Truncated);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page).Length;
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.Equal(1, await store.ExpireModelProfilesAsync(now.AddDays(30), 1, ct));
            var cleanup = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            output.WriteLine($"T10 disposable SQLite 10000 rows: finalize {write.TotalMilliseconds:F3} ms; 100-row page {bytes} bytes; expire one profile {cleanup.TotalMilliseconds:F3} ms. Local synthetic fixture, not a production load benchmark.");
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Additive_upgrade_and_cleanup_preserve_runs_diagnostics_decisions_and_publication_audit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-old-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var store = new SqliteFindingStore(connectionString);
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var key = new ReviewForge.Core.Domain.PrKey("o", "p", "r", 1);
            using var context = new ReviewForge.Core.Pipeline.ReviewContext(key, now, id);
            var run = new ReviewForge.Core.Domain.ReviewRun(id, key, "head", ReviewForge.Core.Domain.ReviewKind.Full,
                now, now, true, [new("finding", "rule", "low", "title", null, null, 42, Published: true)],
                Diagnostics: context.SnapshotDiagnostics());
            await store.SaveRunAsync(run, ct);
            await store.TryAppendFindingDecisionAsync(ReviewForge.Core.Domain.FindingDecision.Record(id, "finding", "head", ReviewForge.Core.Domain.FindingDecisionState.Approved,
                null, "header-key", now, 1), 0, ct);
            // Disposable fixture represents the immediately preceding schema: no profiling tables.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE ModelCalls; DROP TABLE ModelProfiles;";
                await command.ExecuteNonQueryAsync(ct);
            }
            store = new SqliteFindingStore(connectionString);
            Assert.Equal("legacy-unavailable", (await store.ReadModelProfileAsync(id, 0, 10, now, ct)).State);
            var collector = new ModelProfileCollector(id);
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            await store.BeginModelProfileAsync(collector.Snapshot(), ct);
            // A process restart with only the marker is explicitly incomplete, never empty/complete.
            store = new SqliteFindingStore(connectionString);
            Assert.Equal("incomplete", (await store.ReadModelProfileAsync(id, 0, 10, now, ct)).State);
            var interruptedProfileId = collector.ProfileId;
            collector = new ModelProfileCollector(id);
            collector.Resume(await store.BeginModelProfileAsync(collector.Snapshot(), ct));
            Assert.Equal(interruptedProfileId, collector.ProfileId);
            collector.Begin("ExecuteReasoning", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            await store.FinalizeModelProfileAsync(collector.Snapshot(), now, now.AddDays(30), false, ct);
            var resumed = await store.ReadModelProfileAsync(id, 0, 10, now, ct);
            Assert.True(resumed.Incomplete);
            Assert.Equal(interruptedProfileId, Assert.Single(resumed.Calls).ProfileId);
            await store.ExpireModelProfilesAsync(now.AddDays(30), 10, ct);
            var retained = await store.GetRunAsync(id, ct);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(run.Diagnostics), System.Text.Json.JsonSerializer.Serialize(retained!.Diagnostics));
            Assert.Equal(42, Assert.Single(retained.Findings).ThreadId);
            Assert.True(retained.Findings[0].Published);
            Assert.Single(await store.GetFindingDecisionHistoryAsync(id, "finding", 10, ct));
            Assert.Equal(1, (await store.ReadModelProfileAsync(id, 0, 10, now.AddDays(30), ct)).ObservedCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Restart_paging_duplicate_finalization_and_expiry_preserve_summary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.db");
        try
        {
            var collector = new ModelProfileCollector(Guid.NewGuid(), 2);
            collector.Begin("ReviewAgent", ModelCallProfile.Review, "openai", "gpt-5").Complete("completed", null);
            collector.Begin("VerifyFindings", ModelCallProfile.Verifier, "openai", "gpt-5").Complete("failed", null);
            collector.Begin("Fix", ModelCallProfile.Fix, "openai", "gpt-5").Complete("cancelled", null);
            var snapshot = collector.Snapshot();
            IModelProfileStore store = new SqliteFindingStore($"Data Source={path};Pooling=False");
            var ct = TestContext.Current.CancellationToken;
            var now = DateTimeOffset.UtcNow;
            await store.BeginModelProfileAsync(snapshot with { Calls = [] }, ct);
            Assert.True((await store.ReadModelProfileAsync(snapshot.RunId, 0, 1, now, ct)).Incomplete);
            await store.FinalizeModelProfileAsync(snapshot, now, now.AddDays(30), false, ct);
            await store.FinalizeModelProfileAsync(snapshot with { ObservedCalls = 999 }, now, now.AddDays(60), true, ct);
            store = new SqliteFindingStore($"Data Source={path};Pooling=False");
            var page = await store.ReadModelProfileAsync(snapshot.RunId, 0, 1, now, ct);
            Assert.Equal(3, page.ObservedCalls);
            Assert.True(page.Truncated);
            Assert.False(page.Incomplete);
            Assert.Single(page.Calls);
            Assert.Equal(1, page.NextSequence);
            var next = await store.ReadModelProfileAsync(snapshot.RunId, page.NextSequence!.Value, 1, now, ct);
            Assert.Single(next.Calls);
            Assert.Null(next.NextSequence);
            Assert.Equal(0, await store.ExpireModelProfilesAsync(now.AddDays(29), 1, ct));
            Assert.Equal("expired", (await store.ReadModelProfileAsync(snapshot.RunId, 0, 10, now.AddDays(30), ct)).State);
            Assert.Equal(1, await store.ExpireModelProfilesAsync(now.AddDays(30), 1, ct));
            Assert.Equal(0, await store.ExpireModelProfilesAsync(now.AddDays(31), 1, ct));
            page = await store.ReadModelProfileAsync(snapshot.RunId, 0, 10, now.AddDays(31), ct);
            Assert.Empty(page.Calls);
            Assert.Equal(3, page.ObservedCalls);
            Assert.Equal(1, page.Aggregate!.CompletedCalls);
            Assert.Equal(1, page.Aggregate.FailedCalls);
            Assert.Equal(1, page.Aggregate.CancelledCalls);
            Assert.Equal(snapshot.ProfileId, await store.BeginModelProfileAsync(new ModelProfileCollector(snapshot.RunId).Snapshot(), ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadModelProfileAsync(snapshot.RunId, -1, 10, now, ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadModelProfileAsync(snapshot.RunId, 0, 101, now, ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ExpireModelProfilesAsync(now, 101, ct));
            Assert.Equal("legacy-unavailable", (await store.ReadModelProfileAsync(Guid.NewGuid(), 0, 10, now, ct)).State);
        }
        finally { File.Delete(path); }
    }
}
