using Microsoft.Extensions.Options;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;

namespace ReviewForge.Service;

public interface IPipelineBuilder
{
    ReviewPipeline Build(RunKind kind, ReviewRequest request, ReviewContext context);
}

public sealed class ReviewPipelineBuilder(
    IOptions<ReviewOptions> reviewOptions,
    IOptions<ResolveOptions> resolveOptions,
    IOptions<VerifyFindingsOptions> verifyFindingsOptions,
    AutoFixOptions autoFixOptions,
    IGitOps? gitOps,
    AgentFactory agentFactory,
    StageCatalog stageCatalog,
    ResolveContextInitializer resolveContextInitializer,
    ILoggerFactory loggerFactory) : IPipelineBuilder
{
    public ReviewPipeline Build(RunKind kind, ReviewRequest request, ReviewContext context)
    {
        if (request.Kind != kind)
            throw new ArgumentException("The requested run kind does not match the pipeline kind.", nameof(kind));

        var definition = kind switch
        {
            RunKind.Review => Pipelines.Review(
                reviewOptions.Value,
                autoFixOptions,
                verifyFindingsOptions.Value,
                gitOps is not null),
            RunKind.ReviewDraft => Pipelines.ReviewDraft(verifyFindingsOptions.Value),
            RunKind.Resolve => Pipelines.Resolve(resolveOptions.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown run kind."),
        };

        if (kind == RunKind.Resolve)
            resolveContextInitializer.Initialize(request, context);

        var build = new PipelineBuildContext(definition, request, agentFactory.Create());
        var stages = definition.Stages.Select(id => stageCatalog.Create(id, build));
        return new ReviewPipeline(stages, loggerFactory.CreateLogger<ReviewPipeline>());
    }
}