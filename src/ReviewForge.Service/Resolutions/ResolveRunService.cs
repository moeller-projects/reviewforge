using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

public interface IResolveRunService
{
    Task ExecuteAsync(ReviewRequest request, ReviewContext context, CancellationToken ct);
}

public sealed class ResolveRunService(
    IPullRequestSource source,
    IFindingStore store,
    RepoCheckoutPool checkoutPool,
    IChatClientFactory chatClientFactory,
    IGitOps git,
    IProcessRunner processRunner,
    AutoFixOptions autoFix,
    IOptions<ResolveOptions> resolveOptions,
    IOptions<ReviewOptions> reviewOptions,
    IOptions<RepoReadToolsOptions> repoToolsOptions,
    ILoggerFactory loggerFactory,
    TimeProvider clock,
    string? pushPat) : IResolveRunService
{
    public async Task ExecuteAsync(ReviewRequest request, ReviewContext context, CancellationToken ct)
    {
        var opts = resolveOptions.Value;
        if (!opts.Enabled) throw new InvalidOperationException("resolve pipeline is disabled");
        var review = reviewOptions.Value;
        var tools = repoToolsOptions.Value;
        var agent = new NativeReviewAgent(chatClientFactory, new AgentOptions
        {
            MaxContextTokens = review.MaxContextTokens,
            MaxIterations = review.MaxIterations,
            PromptOverridePath = review.PromptOverridePath,
            RuleSetsPath = review.RuleSetsPath,
            Effort = review.ReasoningEffort,
            DebugLogging = review.AgentDebugLogging,
            GrepMaxMs = tools.GrepMaxMs,
            GrepMaxLines = tools.GrepMaxLines,
        }, loggerFactory.CreateLogger<NativeReviewAgent>());

        var privateCheckout = new PrepareRepositoryStage(
            checkoutPool, loggerFactory.CreateLogger<PrepareRepositoryStage>(),
            source: source, clock: clock, checkoutMode: CheckoutMode.Private, autoFix: autoFix);
        var stages = new List<IReviewStage>
        {
            new FetchPrContextStage(source, store),
            new ResolveGateStage(store, opts.AllowedAuthors.ToHashSet(StringComparer.OrdinalIgnoreCase), request.HeadSha,
                loggerFactory.CreateLogger<ResolveGateStage>()),
            privateCheckout,
            new CollectCommentsStage(source, store, opts.AllowedCommenters.ToHashSet(StringComparer.OrdinalIgnoreCase), clock),
            new TriageCommentsStage(agent, opts.TriageBatchSize),
            new PlanFixesStage(opts.MaxThreadsPerRun, opts.MaxWritableFiles),
            new ApplyFixesStage(agent, opts.FixPassMaxIterations),
        };
        if (opts.VerifyCommand is {Length: > 0})
            stages.Add(new VerifyBuildStage(processRunner, opts.VerifyCommand, TimeSpan.FromSeconds(opts.VerifyTimeoutSeconds),
                opts.CommitGranularity.Equals("Single", StringComparison.OrdinalIgnoreCase)));
        stages.Add(new BeginRunStage(store, clock, order: 68));
        stages.Add(new ResolveCommitPushStage(git, store, opts.CommitGranularity,
            autoFix.CommitAuthorName!, autoFix.CommitAuthorEmail!, pushPat,
            loggerFactory.CreateLogger<ResolveCommitPushStage>(), clock));
        stages.Add(new ReplyCommentsStage(source, store, opts.SetFixedStatus,
            loggerFactory.CreateLogger<ReplyCommentsStage>()));
        stages.Add(new PersistRunStage(store, clock));

        context.RunKind = RunKind.Resolve;
        context.RequestedHeadSha = request.HeadSha;
        context.Trigger = request.Trigger;
        context.EnqueueContext = request.EnqueueContext;
        await new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>())
            .RunAsync(context, ct).ConfigureAwait(false);
    }
}