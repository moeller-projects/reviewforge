using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class PlanFixesStageTests
{
    [Fact]
    public async Task Execute_demotes_unauthorized_unanchored_and_out_of_manifest_actionability()
    {
        var ctx = Context(
            Comment(1, "src/a.cs", allowed: false),
            Comment(2, null),
            Comment(3, "src/other.cs"));
        ctx.Resolve!.ThreadVerdicts = [
            Verdict(1, TriageVerdict.Actionable),
            Verdict(2, TriageVerdict.Actionable),
            Verdict(3, TriageVerdict.Actionable)];

        await new PlanFixesStage(10, 10).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.Resolve!.ResolvePlan!.Fixes);
        Assert.All(ctx.Resolve!.ThreadVerdicts, v => Assert.Equal(TriageVerdict.OutOfScope, v.Verdict));
        Assert.Equal(ResolutionOutcome.OutOfScope, ctx.Resolve!.ResolutionOutcomes[1]);
        Assert.Equal("commenter is not authorized to request edits", ctx.Resolve!.ResolutionDetails[1]);
        Assert.Equal("comment has no safe file anchor", ctx.Resolve!.ResolutionDetails[2]);
        Assert.Equal("anchored file is not in the pull-request changed-file manifest", ctx.Resolve!.ResolutionDetails[3]);
    }

    [Fact]
    public async Task Execute_demotes_actionable_verdicts_on_deny_listed_paths()
    {
        var ctx = Context(Comment(4, ".env"));
        ctx.Resolve!.ThreadVerdicts = [Verdict(4, TriageVerdict.Actionable)];

        await new PlanFixesStage(10, 10).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.Resolve!.ResolvePlan!.Fixes);
        Assert.Equal(ResolutionOutcome.OutOfScope, ctx.Resolve!.ResolutionOutcomes[4]);
        Assert.Equal("anchored file is denied by the checkout path policy", ctx.Resolve!.ResolutionDetails[4]);
    }

    [Theory]
    [InlineData(TriageVerdict.NonIssue, ResolutionOutcome.NonIssue)]
    [InlineData(TriageVerdict.Question, ResolutionOutcome.Question)]
    [InlineData(TriageVerdict.AlreadyFixed, ResolutionOutcome.AlreadyFixed)]
    [InlineData(TriageVerdict.OutOfScope, ResolutionOutcome.OutOfScope)]
    public async Task Execute_maps_non_actionable_verdicts_to_reply_outcomes(TriageVerdict verdict, ResolutionOutcome outcome)
    {
        var ctx = Context(Comment(7, "src/a.cs"));
        ctx.Resolve!.ThreadVerdicts = [Verdict(7, verdict)];

        await new PlanFixesStage(10, 10).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(outcome, ctx.Resolve!.ResolutionOutcomes[7]);
        Assert.Empty(ctx.Resolve!.ResolvePlan!.Fixes);
        Assert.Equal(verdict, Assert.Single(ctx.Resolve!.ThreadVerdicts).Verdict);
    }

    [Fact]
    public async Task Execute_records_deferred_outcome_when_planner_budget_overflows()
    {
        var ctx = Context(Comment(1, "src/a.cs"), Comment(2, "src/b.cs"));
        ctx.Resolve!.ThreadVerdicts = [Verdict(1, TriageVerdict.Actionable), Verdict(2, TriageVerdict.Actionable)];

        await new PlanFixesStage(10, 1).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.Resolve!.ResolvePlan!.Fixes);
        Assert.Equal(ResolutionOutcome.Deferred, ctx.Resolve!.ResolutionOutcomes[2]);
        Assert.Equal("writable-file budget", ctx.Resolve!.ResolutionDetails[2]);
    }

    private static ReviewContext Context(params ResolvableComment[] comments)
        => new(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome
            {
                PullRequest = new PullRequest(1, "title", null, "head", "base", "clone", false, "creator", "Creator"),
                ChangedFileManifest = [
                    new ChangedFile("src/a.cs", ChangedFileType.Edit),
                    new ChangedFile("src/b.cs", ChangedFileType.Edit),
                    new ChangedFile(".env", ChangedFileType.Edit)]
            },
            Repository = new RepoPreparation {RepoDir = Path.GetTempPath()},
            Resolve = new ResolveState {ResolvableComments = comments},
        };

    private static ResolvableComment Comment(int id, string? file, bool allowed = true)
        => new(id, file is null ? null : new ThreadAnchor(file, 1, 1), "requester", "Requester", [], $"request-{id}", allowed);
    private static ThreadVerdict Verdict(int id, TriageVerdict verdict)
        => new(id, verdict, "src/a.cs:1", "high");
}
