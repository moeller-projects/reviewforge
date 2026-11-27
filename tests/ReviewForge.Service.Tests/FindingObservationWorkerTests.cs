using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class FindingObservationWorkerTests
{
    [Fact]
    public async Task Strict_verifier_failure_is_persisted_with_full_candidate_but_no_dedupe_identity()
    {
        var key = new PrKey("org", "project", "repo", 1);
        var queue = new ReviewQueue();
        var tracker = new RunTracker();
        var claims = new InFlightClaims(TimeProvider.System, TimeSpan.FromHours(1));
        var store = new FakeFindingStore();
        var request = new ReviewRequest(Guid.NewGuid(), key, DateTimeOffset.UtcNow, Kind: RunKind.ReviewDraft);
        Assert.True(queue.TryEnqueue(request).Accepted);
        using var worker = new ReviewWorker(queue, Options.Create(new HostOptions { WorkerCount = 1 }),
            tracker, new StrictFailurePipeline(), claims, store, NullLogger<ReviewWorker>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await worker.StartAsync(timeout.Token);
        try
        {
            ReviewRun? run = null;
            for (var i = 0; i < 200 && run is null; i++)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
                run = await store.GetRunAsync(request.RunId, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(run);
            Assert.False(run.Success);
            Assert.Equal("ReviewDraft", run.Pipeline);
            Assert.Empty(run.Findings);
            var observation = Assert.Single(run.FindingObservations!);
            Assert.Equal(FindingValidationStatus.Accepted, observation.Validation);
            Assert.Equal(FindingVerificationStatus.Failed, observation.Verification);
            Assert.Equal("malformed-response", observation.VerificationReason);
            Assert.Contains("full claim", observation.FindingJson);
            Assert.Empty(await store.GetKnownDedupeKeysAsync(key, TestContext.Current.CancellationToken));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Single-use boundary double: the worker owns failure finalization, while the real
    // verifier records its failure before throwing. No production pipeline needs IO here.
    private sealed class StrictFailurePipeline : IPipelineBuilder, IReviewStage
    {
        public string Name => "verify-findings";
        public ReviewPipeline Build(RunKind kind, ReviewRequest request, ReviewContext context, IRunProgress? progress = null)
            => new([this], NullLogger<ReviewPipeline>.Instance);

        public async Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
        {
            var finding = new RichFinding
            {
                DedupeKey = "candidate", RuleId = "security/test", Severity = "high", Category = "security",
                Title = "candidate", Description = "full claim",
            };
            ctx.Fetch = ctx.Fetch with { PullRequest = new FakePullRequestSource().Pr };
            ctx.Repository = ctx.Repository with { RepoDir = Path.GetTempPath() };
            ctx.Reasoning.Collector.AddFinding(finding);
            ctx.Validation = ctx.Validation with { AcceptedFindings = [finding] };
            ctx.ObserveValidation(finding, FindingValidationStatus.Accepted, "anchor-in-diff");
            var stage = new VerifyFindingsStage(new FakeChatClientFactory(new ScriptedChatClient(
                ScriptedChatClient.Text("malformed"), ScriptedChatClient.Text("malformed"))), new() { Enabled = true });
            await stage.ExecuteAsync(ctx, ct);
        }
    }
}
