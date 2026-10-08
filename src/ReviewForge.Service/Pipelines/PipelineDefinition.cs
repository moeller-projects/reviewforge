using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;

namespace ReviewForge.Service;

public enum StageId
{
    FetchPrContext,
    ReviewGate,
    ResolveGate,
    PrepareRepository,
    ClassifyRun,
    EnrichContext,
    ExecuteReasoning,
    ValidateFindings,
    VerifyFindings,
    AutoFixFindings,
    BeginRun,
    CommitFixes,
    TriageThreads,
    PublishFindings,
    CollectComments,
    TriageComments,
    PlanFixes,
    ApplyFixes,
    VerifyBuild,
    PersistDraftRun,
    ResolveCommitPush,
    ReplyComments,
    PersistRun,
}

public sealed record PipelineFeatures(
    CheckoutMode CheckoutMode,
    bool IncludeVerifyFindings,
    bool IncludeCommitFixes,
    bool IncludeVerifyBuild,
    ReviewerVote? CleanRunVote);

public sealed record PipelineDefinition(
    RunKind Kind,
    IReadOnlyList<StageId> Stages,
    PipelineFeatures Features);

public static class Pipelines
{
    public static PipelineDefinition Review(
        ReviewOptions review,
        AutoFixOptions autoFix,
        VerifyFindingsOptions verifyFindings,
        bool hasGitOps)
    {
        var cleanRunVote = ParseCleanVote(review.CleanRunVote);

        if (autoFix.IsCommitOnHead && !hasGitOps)
            throw new InvalidOperationException("IGitOps is required when AutoFix:PublishMode is CommitOnHead");

        var features = new PipelineFeatures(
            CheckoutMode: autoFix.IsCommitOnHead ? CheckoutMode.Private : CheckoutMode.Pooled,
            IncludeVerifyFindings: verifyFindings.Enabled,
            IncludeCommitFixes: hasGitOps,
            IncludeVerifyBuild: false,
            CleanRunVote: cleanRunVote);
        var stages = new List<StageId>
        {
            StageId.FetchPrContext,
            StageId.ReviewGate,
            StageId.PrepareRepository,
            StageId.ClassifyRun,
            StageId.EnrichContext,
            StageId.ExecuteReasoning,
            StageId.ValidateFindings,
        };
        if (features.IncludeVerifyFindings)
            stages.Add(StageId.VerifyFindings);
        stages.AddRange([StageId.AutoFixFindings, StageId.BeginRun]);
        if (features.IncludeCommitFixes)
            stages.Add(StageId.CommitFixes);
        stages.AddRange([StageId.TriageThreads, StageId.PublishFindings, StageId.PersistRun]);

        return new PipelineDefinition(RunKind.Review, stages, features);
    }

    public static PipelineDefinition ReviewDraft(VerifyFindingsOptions verifyFindings)
    {
        if (!verifyFindings.Enabled)
            throw new InvalidOperationException("ReviewDraft requires VerifyFindings:Enabled=true");

        var features = new PipelineFeatures(
            CheckoutMode: CheckoutMode.Pooled,
            IncludeVerifyFindings: true,
            IncludeCommitFixes: false,
            IncludeVerifyBuild: false,
            CleanRunVote: null);
        var stages = new List<StageId>
        {
            StageId.FetchPrContext,
            StageId.ReviewGate,
            StageId.PrepareRepository,
            StageId.ClassifyRun,
            StageId.EnrichContext,
            StageId.ExecuteReasoning,
            StageId.ValidateFindings,
            StageId.VerifyFindings,
            StageId.PersistDraftRun,
        };
        return new PipelineDefinition(RunKind.ReviewDraft, stages, features);
    }

    public static PipelineDefinition Resolve(ResolveOptions resolve)
    {
        if (!resolve.Enabled)
            throw new InvalidOperationException("resolve run requested but Resolve:Enabled is false");

        var includeVerifyBuild = resolve.VerifyCommand is {Length: > 0};
        var features = new PipelineFeatures(
            CheckoutMode: CheckoutMode.Private,
            IncludeVerifyFindings: false,
            IncludeCommitFixes: false,
            IncludeVerifyBuild: includeVerifyBuild,
            CleanRunVote: null);
        var stages = new List<StageId>
        {
            StageId.FetchPrContext,
            StageId.ResolveGate,
            StageId.PrepareRepository,
            StageId.CollectComments,
            StageId.TriageComments,
            StageId.PlanFixes,
            StageId.ApplyFixes,
        };
        if (features.IncludeVerifyBuild)
            stages.Add(StageId.VerifyBuild);
        stages.AddRange([StageId.BeginRun, StageId.ResolveCommitPush, StageId.ReplyComments, StageId.PersistRun]);

        return new PipelineDefinition(RunKind.Resolve, stages, features);
    }

    private static ReviewerVote? ParseCleanVote(string value)
        => value.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? null
            : Enum.TryParse<ReviewerVote>(value, ignoreCase: true, out var parsedVote)
              && parsedVote is ReviewerVote.NoResponse or ReviewerVote.Approved or ReviewerVote.ApprovedWithSuggestions
                ? parsedVote
                : throw new InvalidOperationException(
                    $"Review:CleanRunVote '{value}' is invalid; expected NoResponse | Approved | ApprovedWithSuggestions | None");
}