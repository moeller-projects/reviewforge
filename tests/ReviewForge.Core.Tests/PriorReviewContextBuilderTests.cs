using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class PriorReviewContextBuilderTests
{
    private static readonly PrKey Key = new("org", "project", "repo", 1);

    [Fact]
    public void Maps_thread_outcomes_and_missing_threads()
    {
        var findings = new[]
        {
            Finding("open", "r-open", "src/A.cs", 1, 10),
            Finding("fixed", "r-fixed", "src/A.cs", 2, 11),
            Finding("closed", "r-closed", "src/A.cs", 3, 12),
            Finding("gone", "r-gone", "src/A.cs", 4, 99),
        };
        var prior = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, findings.Select(f => f.DedupeKey).ToArray(), findings);
        var threads = new[]
        {
            Thread(10, ReviewThreadStatus.Active),
            Thread(11, ReviewThreadStatus.Fixed),
            Thread(12, ReviewThreadStatus.Closed),
        };

        var result = PriorReviewContextBuilder.Build(prior, threads);

        Assert.NotNull(result);
        Assert.Contains("[open] r-open", result);
        Assert.Contains("[fixed] r-fixed", result);
        Assert.Contains("[closed-by-author] r-closed", result);
        Assert.Contains("[no-longer-visible] r-gone", result);
    }

    [Fact]
    public void Excludes_audit_rows_and_limits_entries()
    {
        var findings = Enumerable.Range(0, PriorReviewContextBuilder.MaxEntries + 5)
            .Select(i => Finding($"k{i}", $"rule-{i:D2}", "src/A.cs", i + 1, null))
            .Append(Finding(AppliedFix.CommandKeyPrefix + "42", "thread-command", "src/A.cs", 1, null))
            .ToArray();
        var prior = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, findings.Select(f => f.DedupeKey).ToArray(), findings);

        var result = PriorReviewContextBuilder.Build(prior, []);

        Assert.NotNull(result);
        Assert.DoesNotContain("thread-command", result);
        Assert.Equal(PriorReviewContextBuilder.MaxEntries,
            result!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
    }

    [Fact]
    public void Applies_payload_cap_and_deterministic_file_line_rule_order()
    {
        var findings = new[]
        {
            Finding("z", "b", "z.cs", 2, null),
            Finding("a", "z", "a.cs", 4, null),
            Finding("b", "a", "a.cs", 2, null),
            Finding("c", "a", "a.cs", 2, null),
            Finding("long", "r", "b.cs", 1, null) with {Title = new string('x', PriorReviewContextBuilder.MaxPayloadChars)},
        };
        var prior = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, findings.Select(f => f.DedupeKey).ToArray(), findings);

        var result = PriorReviewContextBuilder.Build(prior, []);

        Assert.NotNull(result);
        Assert.True(result!.Length <= PriorReviewContextBuilder.MaxPayloadChars);
        Assert.True(result.IndexOf("a at a.cs:2", StringComparison.Ordinal)
            < result.IndexOf("z at a.cs:4", StringComparison.Ordinal));
        Assert.True(result.IndexOf("a at a.cs:2", StringComparison.Ordinal)
            < result.IndexOf("b at z.cs:2", StringComparison.Ordinal));
    }

    [Fact]
    public void Returns_null_without_prior_findings()
    {
        Assert.Null(PriorReviewContextBuilder.Build(null, []));
        Assert.Null(PriorReviewContextBuilder.Build(new PriorRun(Key, "sha", DateTimeOffset.UtcNow, ["key"]), []));
    }

    [Fact]
    public async Task Fetch_stages_memory_only_when_prior_rows_exist()
    {
        var source = new FakePullRequestSource();
        var finding = Finding("key", "security/sql-injection", "src/A.cs", 30, 1);
        var store = new FakeFindingStore
        {
            LastRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, [finding.DedupeKey], [finding]),
        };
        source.Threads.Add(Thread(1, ReviewThreadStatus.Fixed));
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);

        await new FetchPrContextStage(source, store).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Contains("[fixed] security/sql-injection at src/A.cs:30", ctx.ContextStore.Read(PriorReviewContextBuilder.ContextName));

        store.LastRun = null;
        var withoutPrior = new ReviewContext(Key, DateTimeOffset.UtcNow);
        await new FetchPrContextStage(source, store).ExecuteAsync(withoutPrior, CancellationToken.None);
        Assert.Null(withoutPrior.ContextStore.Read(PriorReviewContextBuilder.ContextName));

        store.LastRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, [finding.DedupeKey]);
        var keysOnly = new ReviewContext(Key, DateTimeOffset.UtcNow);
        await new FetchPrContextStage(source, store).ExecuteAsync(keysOnly, CancellationToken.None);
        Assert.Null(keysOnly.ContextStore.Read(PriorReviewContextBuilder.ContextName));
    }

    private static StoredFinding Finding(string key, string rule, string file, int line, int? threadId)
        => new(key, rule, "high", $"Title {key}", file, line, threadId);

    private static ReviewThread Thread(int id, ReviewThreadStatus status)
        => new(id, null, status, []);
}
