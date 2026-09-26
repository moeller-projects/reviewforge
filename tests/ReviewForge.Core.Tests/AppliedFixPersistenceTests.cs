using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using Xunit;

namespace ReviewForge.Core.Tests;

public class AppliedFixPersistenceTests
{
    private static readonly PrKey Key = new("org", "project", "repo", 7);

    [Fact]
    public void Commanded_fix_uses_source_thread_when_posted_thread_is_not_resolved()
    {
        var proposal = new FixProposal(
            "script.sh", 3, 3, "echo \"$name\"", "Quote the variable.",
            FixOrigin.LlmCommanded, SourceThreadId: 42);
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            AppliedFixes = [new AppliedFix("thread-42", proposal, "none")],
        };

        var row = Assert.Single(AppliedFixPersistence.BuildFinalRows(ctx, _ => null));

        Assert.Equal("thread-42", row.DedupeKey);
        Assert.Equal(42, row.ThreadId);
        Assert.NotNull(row.AppliedFixJson);
    }

    [Fact]
    public void Commanded_fix_prefers_resolved_posted_thread_id()
    {
        var proposal = new FixProposal(
            "script.sh", 3, 3, "echo \"$name\"", "Quote the variable.",
            FixOrigin.LlmCommanded, SourceThreadId: 42);
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            AppliedFixes = [new AppliedFix("thread-42", proposal, "none")],
        };

        var row = Assert.Single(AppliedFixPersistence.BuildFinalRows(ctx, _ => 99));

        Assert.Equal(99, row.ThreadId);
    }

    [Fact]
    public void Carried_forward_finding_does_not_keep_prior_run_fix_attribution()
    {
        var prior = new StoredFinding(
            "k1", "rule", "high", "title", "script.sh", 3, 17, "{\"fix\":true}");
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PriorRun = new PriorRun(
                Key, "old-head", DateTimeOffset.UtcNow.AddMinutes(-5), ["k1"], [prior]),
        };

        var row = Assert.Single(AppliedFixPersistence.BuildFinalRows(ctx, _ => null));

        Assert.Equal("k1", row.DedupeKey);
        Assert.Null(row.AppliedFixJson);
        Assert.Equal(17, row.ThreadId);
    }
}
