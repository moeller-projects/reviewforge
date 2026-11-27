using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class FindingObservationTests
{
    private static RichFinding Finding(string key, string rule = "security/test") => new()
    {
        DedupeKey = key, RuleId = rule, Severity = "high", Category = "security",
        Title = "Candidate", Description = "Full claim", Snippet = "original snippet",
    };

    private static ReviewContext Context(params RichFinding[] findings) => new(new("org", "project", "repo", 1), DateTimeOffset.UtcNow)
    {
        Repository = new() { RepoDir = Path.GetTempPath() },
        Reasoning = new() { Result = new ReviewResult { Findings = findings, Narrative = new(), Uncertainties = [] } },
        Validation = new() { AcceptedFindings = findings },
    };

    [Fact]
    public async Task Validation_retains_rejected_full_candidates_and_reasons()
    {
        using var ctx = Context(Finding("rejected"));
        await new ValidateFindingsStage().ExecuteAsync(ctx, CancellationToken.None);
        Assert.Empty(ctx.Validation.AcceptedFindings);
        var observation = Assert.Single(ctx.SnapshotFindingObservations()!);
        Assert.Equal(FindingValidationStatus.Rejected, observation.Validation);
        Assert.Equal("no-anchor", observation.ValidationReason);
        Assert.Equal("Full claim", JsonSerializer.Deserialize<RichFinding>(observation.FindingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Description);
        Assert.Equal(FindingVerificationStatus.Unverified, observation.Verification);
        Assert.Equal(1, FindingObservationCounts.From([observation]).Rejected);
    }

    [Theory]
    [InlineData(false, false, "anchor-unverified")]
    [InlineData(true, false, "not-in-diff")]
    [InlineData(true, true, "anchor-in-diff")]
    public async Task Anchor_validation_outcomes_are_explainable(bool exists, bool changed, string reason)
    {
        var dir = Path.Combine(Path.GetTempPath(), "reviewforge-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string snippet = "var meaningfulValue = ReadImportantValue();";
            if (exists) File.WriteAllText(Path.Combine(dir, "file.cs"), snippet);
            var finding = Finding("candidate") with { Snippet = snippet, Anchor = new("file.cs", 1, 1) };
            using var ctx = Context(finding);
            ctx.Repository = ctx.Repository with { RepoDir = dir };
            ctx.Fetch = ctx.Fetch with { ChangedFileManifest = changed ? [new("file.cs", ChangedFileType.Edit)] : [] };
            await new ValidateFindingsStage().ExecuteAsync(ctx, CancellationToken.None);
            var observation = Assert.Single(ctx.SnapshotFindingObservations());
            Assert.Equal(reason, observation.ValidationReason);
            Assert.Equal(changed ? FindingValidationStatus.Accepted : FindingValidationStatus.Rejected, observation.Validation);
            Assert.Equal("anchor-diff-validation", observation.ValidationProvenance);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("confirmed", FindingVerificationStatus.Confirmed)]
    [InlineData("rejected", FindingVerificationStatus.Rejected)]
    public async Task Lightweight_verdict_retains_reason_and_provenance(string verdict, FindingVerificationStatus expected)
    {
        using var ctx = Context(Finding("candidate"));
        var chat = new ScriptedChatClient(ScriptedChatClient.Text($"[{{\"key\":\"candidate\",\"verdict\":\"{verdict}\",\"reason\":\"evidence\"}}]"));
        await new VerifyFindingsStage(new FakeChatClientFactory(chat), new() { Enabled = true }).ExecuteAsync(ctx, CancellationToken.None);
        var observation = Assert.Single(ctx.SnapshotFindingObservations()!);
        Assert.Equal(expected, observation.Verification);
        Assert.Equal("evidence", observation.VerificationReason);
        Assert.Equal("lightweight-verifier/fast", observation.VerificationProvenance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_verdict_is_not_confirmation_and_is_retained_on_strict_failure(bool strict)
    {
        using var ctx = Context(Finding("candidate"));
        ctx.RunKind = strict ? RunKind.ReviewDraft : RunKind.Review;
        var stage = new VerifyFindingsStage(new FakeChatClientFactory(new ScriptedChatClient(ScriptedChatClient.Text("[]"))), new() { Enabled = true });
        if (strict) await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx, CancellationToken.None));
        else await stage.ExecuteAsync(ctx, CancellationToken.None);
        var observation = Assert.Single(ctx.SnapshotFindingObservations()!);
        Assert.Equal(FindingVerificationStatus.Unverified, observation.Verification);
        Assert.Equal("missing-verdict", observation.VerificationReason);
        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Theory]
    [InlineData(false, """[{"key":"candidate"}]""")]
    [InlineData(true, """[{"key":"candidate"}]""")]
    [InlineData(false, """[{"key":"candidate","verdict":"unknown"}]""")]
    [InlineData(true, """[{"key":"candidate","verdict":"unknown"}]""")]
    public async Task Invalid_verdict_is_unverified_and_fails_strict_drafts(bool strict, string response)
    {
        using var ctx = Context(Finding("candidate"));
        ctx.RunKind = strict ? RunKind.ReviewDraft : RunKind.Review;
        var stage = new VerifyFindingsStage(
            new FakeChatClientFactory(new ScriptedChatClient(ScriptedChatClient.Text(response))), new() { Enabled = true });

        if (strict) await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx, CancellationToken.None));
        else await stage.ExecuteAsync(ctx, CancellationToken.None);

        var observation = Assert.Single(ctx.SnapshotFindingObservations());
        Assert.Equal(FindingVerificationStatus.Unverified, observation.Verification);
        Assert.Equal("missing-verdict", observation.VerificationReason);
        Assert.Single(ctx.Validation.AcceptedFindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_verification_records_failure_before_strict_throw_or_fail_open(bool strict)
    {
        using var ctx = Context(Finding("candidate"));
        ctx.RunKind = strict ? RunKind.ReviewDraft : RunKind.Review;
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("bad"), ScriptedChatClient.Text("bad"));
        var stage = new VerifyFindingsStage(new FakeChatClientFactory(chat), new() { Enabled = true });
        if (strict) await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx, CancellationToken.None));
        else await stage.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(FindingVerificationStatus.Failed, Assert.Single(ctx.SnapshotFindingObservations()!).Verification);
    }

    [Fact]
    public void Deterministic_origin_survives_failure_before_verification_stage()
    {
        var collector = new ReviewCollector();
        collector.AddFinding(Finding("deterministic", "homoglyph/test"));
        var observation = Assert.Single(collector.FindingObservations);
        Assert.Equal(FindingVerificationStatus.Deterministic, observation.Verification);
        Assert.Equal("homoglyph-analyzer", observation.VerificationProvenance);
    }

    [Fact]
    public async Task Disabled_verification_does_not_hide_deterministic_provenance()
    {
        using var ctx = Context(Finding("model"), Finding("deterministic", "homoglyph/test"));
        await new VerifyFindingsStage(new FakeChatClientFactory(new ScriptedChatClient()), new() { Enabled = false }).ExecuteAsync(ctx, CancellationToken.None);
        var observations = ctx.SnapshotFindingObservations()!;
        Assert.Equal("disabled", observations[0].VerificationReason);
        Assert.Equal(FindingVerificationStatus.Unverified, observations[0].Verification);
        Assert.Equal(FindingVerificationStatus.Deterministic, observations[1].Verification);
    }

    [Fact]
    public async Task Cap_and_trivial_skips_are_explicit_and_strict_cap_leaves_observations()
    {
        using var ctx = Context(Finding("first"), Finding("second"));
        var stage = new VerifyFindingsStage(new FakeChatClientFactory(new ScriptedChatClient(ScriptedChatClient.Text("[]"))), new() { Enabled = true, MaxFindings = 1 });
        await stage.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal("finding-cap", ctx.SnapshotFindingObservations()![1].VerificationReason);
        ctx.RunKind = RunKind.ReviewDraft;
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx, CancellationToken.None));
        Assert.All(ctx.SnapshotFindingObservations()!, o => Assert.Equal("finding-cap", o.VerificationReason));
        ctx.Reasoning = ctx.Reasoning with { Result = ctx.RequireResult() with { ReviewDepth = "trivial diff — no agent run" } };
        await stage.ExecuteAsync(ctx, CancellationToken.None);
        Assert.All(ctx.SnapshotFindingObservations()!, o => Assert.Equal("trivial-diff", o.VerificationReason));
    }

    [Fact]
    public async Task Prompt_budget_omissions_are_unverified_and_strict_failure_keeps_the_snapshot()
    {
        using var ctx = Context(Finding("candidate"));
        var stage = new VerifyFindingsStage(new FakeChatClientFactory(new ScriptedChatClient()), new() { Enabled = true, MaxPromptChars = 80 });
        await stage.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal("prompt-budget", Assert.Single(ctx.SnapshotFindingObservations()).VerificationReason);
        ctx.RunKind = RunKind.ReviewDraft;
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Equal("prompt-budget", Assert.Single(ctx.SnapshotFindingObservations()).VerificationReason);
    }

    [Fact]
    public async Task Verifier_cannot_reject_deterministic_or_cap_omitted_findings()
    {
        using var ctx = Context(Finding("first"), Finding("second"), Finding("deterministic", "homoglyph/test"));
        var chat = new ScriptedChatClient(ScriptedChatClient.Text("""[{"key":"first","verdict":"confirmed"},{"key":"second","verdict":"rejected"},{"key":"deterministic","verdict":"rejected"}]"""));
        await new VerifyFindingsStage(new FakeChatClientFactory(chat), new() { Enabled = true, MaxFindings = 1 }).ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal(3, ctx.Validation.AcceptedFindings.Count);
        Assert.Equal("finding-cap", ctx.SnapshotFindingObservations()[1].VerificationReason);
        Assert.Equal(FindingVerificationStatus.Deterministic, ctx.SnapshotFindingObservations()[2].Verification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalization_persists_rejected_observations_without_identity_rows(bool draft)
    {
        using var ctx = Context(Finding("rejected"));
        ctx.Fetch = ctx.Fetch with { PullRequest = new FakePullRequestSource().Pr };
        ctx.RunKind = draft ? RunKind.ReviewDraft : RunKind.Review;
        await new ValidateFindingsStage().ExecuteAsync(ctx, CancellationToken.None);
        var store = new FakeFindingStore();
        if (draft) await new PersistDraftRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
        else
        {
            await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
            Assert.Single(Assert.Single(store.Runs).FindingObservations!);
            await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);
        }
        var run = Assert.Single(store.Runs);
        Assert.True(run.Success);
        Assert.Empty(run.Findings);
        Assert.Equal("no-anchor", Assert.Single(run.FindingObservations!).ValidationReason);
        Assert.Empty(await store.GetKnownDedupeKeysAsync(ctx.Pr, CancellationToken.None));
    }

    [Fact]
    public void Counts_do_not_mistake_carried_or_reconciled_publications_for_new_posts()
    {
        var candidate = FindingObservation.Create(Finding("candidate")) with { Validation = FindingValidationStatus.Accepted };
        var known = FindingObservation.Create(Finding("known"), previouslyKnown: true);
        var counts = FindingObservationCounts.From([candidate, known]);
        Assert.Equal(2, counts.Candidates);
        Assert.Equal(1, counts.Accepted);
        Assert.Equal(1, counts.PreviouslyKnown);
        Assert.Equal(0, counts.NewlyPosted);
        Assert.Equal(1, FindingObservationCounts.From([candidate with { NewlyPosted = true }, known]).NewlyPosted);
    }

    [Fact]
    public void Collector_snapshot_survives_incomplete_reasoning_and_preserves_original_payload()
    {
        var collector = new ReviewCollector(["known"]);
        var finding = Finding("new");
        collector.AddFinding(finding);
        collector.RecordSuppressedFinding(Finding("known"));
        finding.Anchor = new("changed-later.cs", 1, 1);
        using var ctx = Context();
        ctx.Reasoning = ctx.Reasoning with { Collector = collector, Result = null };
        var observations = ctx.SnapshotFindingObservations()!;
        Assert.Contains("Full claim", observations[0].FindingJson);
        Assert.DoesNotContain("changed-later.cs", observations[0].FindingJson);
        var counts = FindingObservationCounts.From(observations);
        Assert.Equal(2, counts.Candidates);
        Assert.Equal(1, counts.PreviouslyKnown);
        Assert.Equal(0, counts.Accepted);
    }
}
