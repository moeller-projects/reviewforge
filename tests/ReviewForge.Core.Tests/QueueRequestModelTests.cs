using System.Diagnostics;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class QueueRequestModelTests
{
    [Fact]
    public void Review_request_preserves_discovery_metadata()
    {
        var runId = Guid.NewGuid();
        var pr = new PrKey("org", "project", "repo", 42);
        var enqueuedAt = DateTimeOffset.UtcNow;
        var activity = new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        var request = new ReviewRequest(
            runId, pr, enqueuedAt, activity, "head-sha", EnqueueTrigger.Discovery, RunKind.Review);

        Assert.Equal(runId, request.RunId);
        Assert.Equal(pr, request.Pr);
        Assert.Equal(enqueuedAt, request.EnqueuedAt);
        Assert.Equal(activity, request.EnqueueContext);
        Assert.Equal("head-sha", request.HeadSha);
        Assert.Equal(EnqueueTrigger.Discovery, request.Trigger);
        Assert.Equal(RunKind.Review, request.Kind);

        var result = new EnqueueResult(true, 3);
        Assert.True(result.Accepted);
        Assert.Equal(3, result.QueueDepth);
    }
}
