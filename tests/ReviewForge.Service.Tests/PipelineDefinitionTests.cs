using System.Diagnostics;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Ports;
using Xunit;

namespace ReviewForge.Service.Tests;

public sealed class PipelineDefinitionTests
{
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    [Fact]
    public void Review_definition_preserves_stage_order_and_optional_stage_positions()
    {
        var standard = Pipelines.Review(
            new ReviewOptions(), new AutoFixOptions(), new VerifyFindingsOptions(), hasGitOps: true);
        var verified = Pipelines.Review(
            new ReviewOptions(), new AutoFixOptions(), new VerifyFindingsOptions {Enabled = true}, hasGitOps: true);

        Assert.Equal(
            [
                StageId.FetchPrContext, StageId.ReviewGate, StageId.PrepareRepository, StageId.ClassifyRun,
                StageId.EnrichContext, StageId.ExecuteReasoning, StageId.ValidateFindings,
                StageId.AutoFixFindings, StageId.BeginRun, StageId.CommitFixes, StageId.TriageThreads,
                StageId.PublishFindings, StageId.PersistRun,
            ],
            standard.Stages);
        Assert.Equal(
            [
                StageId.FetchPrContext, StageId.ReviewGate, StageId.PrepareRepository, StageId.ClassifyRun,
                StageId.EnrichContext, StageId.ExecuteReasoning, StageId.ValidateFindings, StageId.VerifyFindings,
                StageId.AutoFixFindings, StageId.BeginRun, StageId.CommitFixes, StageId.TriageThreads,
                StageId.PublishFindings, StageId.PersistRun,
            ],
            verified.Stages);
    }

    [Fact]
    public void Review_definition_omits_unavailable_commit_stage_and_rejects_required_git_ops()
    {
        var standard = Pipelines.Review(
            new ReviewOptions(), new AutoFixOptions(), new VerifyFindingsOptions(), hasGitOps: false);
        Assert.DoesNotContain(StageId.CommitFixes, standard.Stages);

        var commitOnHead = new AutoFixOptions {Enabled = true, PublishMode = AutoFixOptions.ModeCommitOnHead};
        Assert.Throws<InvalidOperationException>(() =>
            Pipelines.Review(new ReviewOptions(), commitOnHead, new VerifyFindingsOptions(), hasGitOps: false));
    }

    [Fact]
    public void Resolve_definition_preserves_stage_order_and_optional_build_verification()
    {
        var standard = Pipelines.Resolve(new ResolveOptions {Enabled = true});
        var verified = Pipelines.Resolve(new ResolveOptions {Enabled = true, VerifyCommand = ["dotnet", "test"]});

        Assert.Equal(
            [
                StageId.FetchPrContext, StageId.ResolveGate, StageId.PrepareRepository, StageId.CollectComments,
                StageId.TriageComments, StageId.PlanFixes, StageId.ApplyFixes, StageId.BeginRun,
                StageId.ResolveCommitPush, StageId.ReplyComments, StageId.PersistRun,
            ],
            standard.Stages);
        Assert.Equal(
            [
                StageId.FetchPrContext, StageId.ResolveGate, StageId.PrepareRepository, StageId.CollectComments,
                StageId.TriageComments, StageId.PlanFixes, StageId.ApplyFixes, StageId.VerifyBuild,
                StageId.BeginRun, StageId.ResolveCommitPush, StageId.ReplyComments, StageId.PersistRun,
            ],
            verified.Stages);
    }

    [Fact]
    public void Resolve_definition_rejects_disabled_pipeline()
    {
        Assert.Throws<InvalidOperationException>(() => Pipelines.Resolve(new ResolveOptions()));
    }

    [Fact]
    public void Resolve_context_initialization_pins_request_head_and_trace_context()
    {
        var enqueueContext = new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var request = new ReviewRequest(
            Guid.NewGuid(), Key, DateTimeOffset.UtcNow,
            HeadSha: "expected-head", Trigger: EnqueueTrigger.ResolveCommand,
            Kind: RunKind.Resolve, EnqueueContext: enqueueContext);
        using var context = new ReviewContext(Key, request.EnqueuedAt, request.RunId);

        new ResolveContextInitializer().Initialize(request, context);

        Assert.Equal(RunKind.Resolve, context.RunKind);
        Assert.Equal("expected-head", context.Resolve!.RequestedHeadSha);
        Assert.Equal(EnqueueTrigger.ResolveCommand, context.Trigger);
        Assert.Equal(enqueueContext, context.EnqueueContext);
    }

    [Fact]
    public void Resolve_context_initializer_rejects_review_request()
    {
        var request = new ReviewRequest(Guid.NewGuid(), Key, DateTimeOffset.UtcNow);
        using var context = new ReviewContext(Key, request.EnqueuedAt, request.RunId);

        Assert.Throws<ArgumentException>(() =>
        {
            new ResolveContextInitializer().Initialize(request, context);
        });
        Assert.Null(context.Resolve);
    }

    [Fact]
    public void Push_credentials_redact_sensitive_values_when_formatted()
    {
        var credentials = new PushCredentials("secret-pat", "secret-author", "secret@example.test");

        var formatted = credentials.ToString();

        Assert.DoesNotContain("secret-pat", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-author", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.test", formatted, StringComparison.Ordinal);
    }
}
