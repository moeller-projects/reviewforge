using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;

namespace ReviewForge.Service;

public sealed class PipelineBuildContext(
    PipelineDefinition definition,
    ReviewRequest request,
    NativeReviewAgent agent)
{
    public PipelineDefinition Definition { get; } = definition;
    public ReviewRequest Request { get; } = request;
    public NativeReviewAgent Agent { get; } = agent;
}

public sealed class StageCatalog
{
    private readonly IPullRequestSource _Source;
    private readonly IFindingStore _Store;
    private readonly RepoCheckoutPool _CheckoutPool;
    private readonly IChatClientFactory _ChatClientFactory;
    private readonly IContextEnricher? _Enricher;
    private readonly TimeProvider _Clock;
    private readonly ILoggerFactory _LoggerFactory;
    private readonly ReviewOptions _ReviewOptions;
    private readonly ResolveOptions _ResolveOptions;
    private readonly WorkspaceOptions _WorkspaceOptions;
    private readonly AutoFixOptions _AutoFixOptions;
    private readonly VerifyFindingsOptions _VerifyFindingsOptions;
    private readonly FindingFixerRegistry _FixerRegistry;
    private readonly IGitOps? _GitOps;
    private readonly IProcessRunner _ProcessRunner;
    private readonly PushCredentials _PushCredentials;
    private readonly DiffBudget _DiffBudget;
    private readonly string _FindingsDir;

    public StageCatalog(
        IPullRequestSource source,
        IFindingStore store,
        RepoCheckoutPool checkoutPool,
        IChatClientFactory chatClientFactory,
        IContextEnricher? enricher,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        IOptions<ReviewOptions> reviewOptions,
        IOptions<ResolveOptions> resolveOptions,
        IOptions<WorkspaceOptions> workspaceOptions,
        IOptions<AutoFixOptions> autoFixOptions,
        IOptions<VerifyFindingsOptions> verifyFindingsOptions,
        IEnumerable<IFindingFixer> findingFixers,
        IGitOps? gitOps,
        IProcessRunner processRunner,
        PushCredentials pushCredentials)
    {
        _Source = source;
        _Store = store;
        _CheckoutPool = checkoutPool;
        _ChatClientFactory = chatClientFactory;
        _Enricher = enricher;
        _Clock = clock;
        _LoggerFactory = loggerFactory;
        _ReviewOptions = reviewOptions.Value;
        _ResolveOptions = resolveOptions.Value;
        _WorkspaceOptions = workspaceOptions.Value;
        _AutoFixOptions = autoFixOptions.Value;
        _VerifyFindingsOptions = verifyFindingsOptions.Value;
        _FixerRegistry = new FindingFixerRegistry(findingFixers);
        _GitOps = gitOps;
        _ProcessRunner = processRunner;
        _PushCredentials = pushCredentials;
        _DiffBudget = new DiffBudget(
            _ReviewOptions.MaxDiffBytes,
            _ReviewOptions.MaxDiffBytesPerFile,
            _ReviewOptions.DiffExcludeGlobs ?? DiffBudget.Default.ExcludeGlobs);
        _FindingsDir = Path.Combine(_WorkspaceOptions.WorkDir, "findings");

        var logger = loggerFactory.CreateLogger<StageCatalog>();
        foreach (var ruleId in _AutoFixOptions.AllowedRuleIds)
        {
            if (!_FixerRegistry.TryGet(ruleId, out _))
                logger.LogWarning(
                    "AutoFix:AllowedRuleIds entry '{RuleId}' has no registered fixer — it can never match", ruleId);
        }
    }

    public IReviewStage Create(StageId id, PipelineBuildContext build)
    {
        var features = build.Definition.Features;
        return id switch
        {
            StageId.FetchPrContext => new FetchPrContextStage(_Source, _Store),
            StageId.ReviewGate => new ReviewGateStage(_Clock),
            StageId.ResolveGate => new ResolveGateStage(
                _Store,
                _ResolveOptions.AllowedAuthors.ToHashSet(StringComparer.OrdinalIgnoreCase),
                build.Request.HeadSha,
                _LoggerFactory.CreateLogger<ResolveGateStage>()),
            StageId.PrepareRepository => new PrepareRepositoryStage(
                _CheckoutPool,
                _LoggerFactory.CreateLogger<PrepareRepositoryStage>(),
                _DiffBudget,
                _Source,
                _Enricher,
                _Clock,
                checkoutMode: features.CheckoutMode,
                autoFix: _AutoFixOptions),
            StageId.ClassifyRun => new ClassifyRunStage(_Source),
            StageId.EnrichContext => new EnrichContextStage(
                _Enricher, _LoggerFactory.CreateLogger<EnrichContextStage>()),
            StageId.ExecuteReasoning => new ExecuteReasoningStage(
                build.Agent,
                _ReviewOptions.MaxDiffChars,
                _ReviewOptions.MaxDiffCharsPerFile,
                _FindingsDir,
                trivialDiffSkipEnabled: _ReviewOptions.TrivialDiffSkipEnabled),
            StageId.ValidateFindings => new ValidateFindingsStage(
                _LoggerFactory.CreateLogger<ValidateFindingsStage>()),
            StageId.VerifyFindings => new VerifyFindingsStage(
                _ChatClientFactory,
                _VerifyFindingsOptions,
                _LoggerFactory.CreateLogger<VerifyFindingsStage>()),
            StageId.AutoFixFindings => new AutoFixFindingsStage(
                _FixerRegistry,
                build.Agent,
                _AutoFixOptions,
                _LoggerFactory.CreateLogger<AutoFixFindingsStage>(),
                store: _Store),
            StageId.BeginRun => new BeginRunStage(_Store, _Clock),
            StageId.CommitFixes => new CommitFixesStage(
                _GitOps ?? throw new InvalidOperationException("IGitOps is required for the commit-fixes stage"),
                _Store,
                _AutoFixOptions,
                _LoggerFactory.CreateLogger<CommitFixesStage>(),
                _PushCredentials.Pat),
            StageId.TriageThreads => new TriageThreadsStage(
                _Source, _LoggerFactory.CreateLogger<TriageThreadsStage>()),
            StageId.PublishFindings => new PublishFindingsStage(
                _Source,
                _Store,
                _LoggerFactory.CreateLogger<PublishFindingsStage>(),
                features.CleanRunVote,
                _AutoFixOptions),
            StageId.CollectComments => new CollectCommentsStage(
                _Source,
                _Store,
                _ResolveOptions.AllowedCommenters.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _Clock),
            StageId.TriageComments => new TriageCommentsStage(
                build.Agent, _ResolveOptions.TriageBatchSize),
            StageId.PlanFixes => new PlanFixesStage(
                _ResolveOptions.MaxThreadsPerRun, _ResolveOptions.MaxWritableFiles),
            StageId.ApplyFixes => new ApplyFixesStage(
                build.Agent, _ResolveOptions.FixPassMaxIterations),
            StageId.VerifyBuild => new VerifyBuildStage(
                _ProcessRunner,
                _ResolveOptions.VerifyCommand ?? [],
                TimeSpan.FromSeconds(_ResolveOptions.VerifyTimeoutSeconds),
                _ResolveOptions.CommitGranularity.Equals("Single", StringComparison.OrdinalIgnoreCase)),
            StageId.ResolveCommitPush => new ResolveCommitPushStage(
                _GitOps ?? throw new InvalidOperationException("IGitOps is required for resolve runs"),
                _Store,
                _ResolveOptions.CommitGranularity,
                _PushCredentials.AuthorName!,
                _PushCredentials.AuthorEmail!,
                _PushCredentials.Pat,
                _LoggerFactory.CreateLogger<ResolveCommitPushStage>(),
                _Clock),
            StageId.ReplyComments => new ReplyCommentsStage(
                _Source,
                _Store,
                _ResolveOptions.SetFixedStatus,
                _LoggerFactory.CreateLogger<ReplyCommentsStage>()),
            StageId.PersistRun => new PersistRunStage(_Store, _Clock),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown review stage."),
        };
    }
}
