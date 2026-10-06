using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Workspaces;
using ReviewForge.Service;
using ReviewForge.Testing;

namespace ReviewForge.Service.Tests;

internal static class PipelineBuilderTestFactory
{
    public static IPipelineBuilder Create(
        FakePullRequestSource source,
        FakeFindingStore store,
        IGitOps git,
        IChatClientFactory chatClientFactory,
        string workDir,
        ReviewOptions? review = null,
        ResolveOptions? resolve = null,
        AutoFixOptions? autoFix = null,
        IEnumerable<IFindingFixer>? findingFixers = null)
    {
        var reviewOptions = Options.Create(review ?? new ReviewOptions());
        var resolveOptions = Options.Create(resolve ?? new ResolveOptions());
        var autoFixOptions = autoFix ?? new AutoFixOptions
        {
            CommitAuthorName = "ReviewForge",
            CommitAuthorEmail = "bot@example.test",
        };
        var verifyOptions = Options.Create(new VerifyFindingsOptions());
        var workspaceOptions = Options.Create(new WorkspaceOptions { WorkDir = workDir });
        var checkoutPool = new RepoCheckoutPool(git, new FakeWorkspaceFs(), workDir);
        var loggerFactory = NullLoggerFactory.Instance;
        var credentials = new PushCredentials("test-pat", autoFixOptions.CommitAuthorName, autoFixOptions.CommitAuthorEmail);
        var catalog = new StageCatalog(
            source,
            store,
            checkoutPool,
            chatClientFactory,
            enricher: null,
            TimeProvider.System,
            loggerFactory,
            reviewOptions,
            resolveOptions,
            workspaceOptions,
            Options.Create(autoFixOptions),
            verifyOptions,
            findingFixers ?? [],
            git,
            new UnusedProcessRunner(),
            credentials);
        var agentFactory = new AgentFactory(chatClientFactory, reviewOptions, loggerFactory);
        return new ReviewPipelineBuilder(
            reviewOptions,
            resolveOptions,
            verifyOptions,
            autoFixOptions,
            git,
            agentFactory,
            catalog,
            new ResolveContextInitializer(),
            loggerFactory);
    }

    private sealed class UnusedProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Build verification was not expected in this test.");
    }
}
