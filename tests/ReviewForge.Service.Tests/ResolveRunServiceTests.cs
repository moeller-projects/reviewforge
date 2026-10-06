using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class ResolveRunServiceTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "reviewforge-resolve-service-" + Guid.NewGuid().ToString("N"));
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public async Task Execute_builds_resolve_pipeline_and_stops_at_no_comments_gate()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var service = CreateService(source, store, new ResolveOptions
        {
            Enabled = true,
            AllowedAuthors = ["creator-1"],
        });
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow,
            HeadSha: "head-sha", Trigger: EnqueueTrigger.Discovery, Kind: RunKind.Resolve);
        var context = new ReviewContext(Key, request.EnqueuedAt, request.RunId);

        await service.ExecuteAsync(request, context, CancellationToken.None);

        Assert.Equal(RunKind.Resolve, context.RunKind);
        Assert.Equal("head-sha", context.RequestedHeadSha);
        Assert.Equal(EnqueueTrigger.Discovery, context.Trigger);
        Assert.Equal(ResolveGateDecision.NoComments.ToString(), context.TerminationReason);
        Assert.Empty(store.Runs);
    }

    [Fact]
    public async Task Execute_rejects_when_resolve_pipeline_is_disabled()
    {
        var service = CreateService(new FakePullRequestSource(), new FakeFindingStore(), new ResolveOptions());
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow, Kind: RunKind.Resolve);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(request, new ReviewContext(Key, request.EnqueuedAt, request.RunId), CancellationToken.None));
    }

    private ResolveRunService CreateService(FakePullRequestSource source, FakeFindingStore store, ResolveOptions options)
    {
        var git = new FakeGitOps();
        var pool = new RepoCheckoutPool(git, new FakeWorkspaceFs(), _workDir);
        return new ResolveRunService(
            source,
            store,
            pool,
            new FakeChatClientFactory(new ScriptedChatClient()),
            git,
            new UnusedProcessRunner(),
            new AutoFixOptions { CommitAuthorName = "ReviewForge", CommitAuthorEmail = "bot@example.test" },
            Options.Create(options),
            Options.Create(new ReviewOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            pushPat: null);
    }

    private sealed class UnusedProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The resolve gate must terminate before build verification.");
    }
}
