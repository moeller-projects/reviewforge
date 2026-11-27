using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class RunSummaryTests
{
    [Fact]
    public void Summary_preserves_durable_identity_outcome_chronology_and_count_without_finding_payloads()
    {
        var pr = new PrKey("org", "project", "repo", 1);
        var query = new CurrentHeadQuery(pr, "head");
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var summary = new RunSummary(id, query.Pr, query.HeadSha, "Review", start, null, false, 2);
        Assert.Equal(id, summary.RunId);
        Assert.Equal(pr, summary.Pr);
        Assert.Equal("head", summary.HeadSha);
        Assert.Equal("Review", summary.Pipeline);
        Assert.Equal(start, summary.StartedAt);
        Assert.Null(summary.CompletedAt);
        Assert.False(summary.Success);
        Assert.Equal(2, summary.ReviewFindings);
    }
}
