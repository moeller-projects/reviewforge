using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class RunDiagnosticsTests
{
    [Fact]
    public void Snapshot_is_null_safe_and_can_capture_partial_collector_context_after_failure()
    {
        using var ctx = new ReviewContext(new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        var empty = ctx.SnapshotDiagnostics();
        Assert.Null(empty.Context.ChangedFileCount);
        Assert.Null(empty.Context.ReviewableFileCount);
        Assert.Null(empty.Context.ReviewDepth);
        Assert.Equal("unavailable", empty.Context.ModelProvenance);
        ctx.DiagnosticRuleBookVersion = "captured-rules";
        ctx.Reasoning.Collector.Complete(new ReviewNarrative { ReviewSummary = "partial summary" });
        ctx.Reasoning.Collector.AddUncertainty(new("topic", "question", null));
        var partial = ctx.SnapshotDiagnostics();
        Assert.Equal("captured-rules", partial.Context.RuleBookVersion);
        Assert.Equal("partial summary", partial.Context.ReviewSummary);
        Assert.Single(partial.Context.Uncertainties);
        Assert.False(partial.Context.CollectionsTruncated);
    }

    [Theory]
    [InlineData("Basic", "dXNlcjpwYXNz")]
    [InlineData("Bearer", "private-bearer")]
    [InlineData("Negotiate", "reversible-credential")]
    [InlineData("Digest", "username=person, response=reversible-credential")]
    public void Authorization_redaction_consumes_the_entire_header_value(string scheme, string credential)
    {
        using var ctx = new ReviewContext(new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        ctx.Reasoning.Collector.Complete(new ReviewNarrative { ReviewSummary = $"Authorization: {scheme} {credential}\nSafe explanation" });
        var summary = ctx.SnapshotDiagnostics().Context.ReviewSummary!;
        Assert.DoesNotContain(credential, summary);
        Assert.Contains("[redacted]", summary);
        Assert.Contains("Safe explanation", summary);
    }

    [Fact]
    public void Snapshot_whitelists_caps_redacts_and_copies_mutable_execution_state()
    {
        using var ctx = new ReviewContext(new("o", "p", "r", 1), DateTimeOffset.UtcNow);
        ctx.Fetch = new() { PullRequest = new FakePullRequestSource().Pr };
        ctx.PlannedStages = ["one", "two"];
        ctx.StageExecutions.Add(new("one", ctx.StartedAt, null, null, "running", null));
        ctx.Repository = new() { ReviewableFiles = ["a.cs"] };
        ctx.Reasoning = new() { Result = new ReviewResult {
            RuleBookVersion = "v1", ReviewDepth = "depth",
            Narrative = new() {
                ReviewSummary = new string('a', 2500),
                VerificationSummary = "password=do-not-serialize",
                PrSummary = "https://user:secret@host/path",
                ThreadActions = [new(1, ThreadActionKind.Answer, "not-a-diagnostic")],
                AcceptanceCriteria = Enumerable.Range(1, 25).Select(i => new AcVerdict(i, "criterion", AcStatus.Unclear, "evidence")).ToArray() },
            Findings = [], Uncertainties = Enumerable.Range(1, 25).Select(i => new ReviewUncertainty("topic", "question", "context")).ToArray() } };
        var snapshot = ctx.SnapshotDiagnostics();
        ctx.StageExecutions.Clear();
        Assert.Single(snapshot.Stages);
        Assert.Equal(2, snapshot.PlannedStages.Count);
        Assert.Equal(1, snapshot.Context.ReviewableFileCount);
        Assert.Equal(ctx.Fetch.PullRequest.SourceCommitSha, snapshot.Context.SourceRevision);
        Assert.Equal(ctx.Fetch.PullRequest.TargetCommitSha, snapshot.Context.TargetRevision);
        Assert.Equal(0, snapshot.Context.ChangedFileCount);
        Assert.Equal("v1", snapshot.Context.RuleBookVersion);
        Assert.Equal("depth", snapshot.Context.ReviewDepth);
        Assert.EndsWith("[truncated]", snapshot.Context.ReviewSummary);
        Assert.Equal(20, snapshot.Context.Uncertainties.Count);
        Assert.Equal(20, snapshot.Context.AcceptanceCriteria.Count);
        Assert.True(snapshot.Context.CollectionsTruncated);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("do-not-serialize", json);
        Assert.DoesNotContain("user:secret", json);
        Assert.DoesNotContain("not-a-diagnostic", json);
    }
}
