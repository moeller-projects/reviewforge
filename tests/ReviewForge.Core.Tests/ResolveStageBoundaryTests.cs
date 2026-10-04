using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ResolveStageBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reviewforge-resolve-stage-" + Guid.NewGuid().ToString("N"));
    private static readonly PrKey Key = new("o", "p", "r", 1);

    public ResolveStageBoundaryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private ReviewContext Context(Guid? runId = null)
    {
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow, runId) { RepoDir = _root };
        ctx.PullRequest = new PullRequest(1, "title", null, "head-sha", "base", "https://clone", false, "creator", "Creator", "refs/heads/feature");
        return ctx;
    }

    private static ResolvableComment Comment(int id, bool allowed = true, ThreadAnchor? anchor = null)
        => new(id, anchor ?? new ThreadAnchor("src/A.cs", 1, 1), "human", "Human", [], "please fix", allowed);

    [Fact]
    public async Task Triage_normalizes_duplicate_missing_unknown_and_evidence_less_pushback()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.FunctionCalls(
            ("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 1, ["verdict"] = "NonIssue", ["evidence"] = "no citation", ["confidence"] = "high" }),
            ("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 1, ["verdict"] = "Actionable", ["evidence"] = "A.cs:1", ["confidence"] = "high" }),
            ("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 999, ["verdict"] = "Actionable", ["evidence"] = "A.cs:1", ["confidence"] = "high" }),
            ("TaskDone", new Dictionary<string, object?> { ["summary"] = "triaged" })));
        var ctx = Context();
        ctx.ResolvableComments = [Comment(1), Comment(2)];

        await new TriageCommentsStage(new NativeReviewAgent(new FakeChatClientFactory(chat))).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, ctx.ThreadVerdicts.Count);
        Assert.Equal(TriageVerdict.OutOfScope, ctx.ThreadVerdicts[0].Verdict);
        Assert.Equal("duplicate verdict", ctx.ThreadVerdicts[0].Evidence);
        Assert.Equal(TriageVerdict.OutOfScope, ctx.ThreadVerdicts[1].Verdict);
        Assert.DoesNotContain(ctx.ThreadVerdicts, v => v.ThreadId == 999);
    }

    [Fact]
    public async Task Triage_downgrades_evidence_less_already_fixed_and_accepts_file_line_evidence()
    {
        var chat = new ScriptedChatClient(ScriptedChatClient.FunctionCalls(
            ("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 1, ["verdict"] = "AlreadyFixed", ["evidence"] = "current code is fine", ["confidence"] = "medium" }),
            ("RecordVerdict", new Dictionary<string, object?> { ["threadId"] = 2, ["verdict"] = "NonIssue", ["evidence"] = "src/A.cs:12 contradicts this", ["confidence"] = "high" }),
            ("TaskDone", new Dictionary<string, object?> { ["summary"] = "done" })));
        var ctx = Context();
        ctx.ResolvableComments = [Comment(1), Comment(2)];

        await new TriageCommentsStage(new NativeReviewAgent(new FakeChatClientFactory(chat))).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(TriageVerdict.OutOfScope, ctx.ThreadVerdicts[0].Verdict);
        Assert.Equal("evidence did not include a file:line citation", ctx.ThreadVerdicts[0].Evidence);
        Assert.Equal(TriageVerdict.NonIssue, ctx.ThreadVerdicts[1].Verdict);
    }

    [Fact]
    public async Task Apply_fixes_reverts_edits_when_task_done_is_missing_and_declines_cluster()
    {
        var path = Path.Combine(_root, "A.cs");
        File.WriteAllText(path, "old\n");
        var hash = HashLine.Of("old");
        var chat = new ScriptedChatClient(ScriptedChatClient.FunctionCalls(
            ("EditFile", new Dictionary<string, object?> { ["path"] = "A.cs", ["edits"] = new[] { new LineEdit(hash, null, null, null, "new") } })));
        var ctx = Context();
        ctx.ResolvePlan = new ResolvePlan([new PlannedFix(1, new ThreadAnchor("A.cs", 1, 1), "fix", "A.cs:1", ["A.cs"])], new HashSet<string> { "A.cs" }, []);

        await new ApplyFixesStage(new NativeReviewAgent(new FakeChatClientFactory(chat)), 2).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("old\n", File.ReadAllText(path));
        Assert.Equal(ResolutionOutcome.AgentDeclined, ctx.ResolutionOutcomes[1]);
        Assert.Contains("did not complete task_done", ctx.ResolutionDetails[1]);
        Assert.Empty(ctx.AppliedResolutions);
    }

    [Fact]
    public async Task Verify_failure_reverts_files_and_single_commit_downgrades_survivors()
    {
        var first = Path.Combine(_root, "A.cs");
        var second = Path.Combine(_root, "B.cs");
        File.WriteAllText(first, "a\n"); File.WriteAllText(second, "b\n");
        var e1 = new HashLineEditor(new RepoPathGuard(_root), new HashSet<string> { "A.cs" });
        var e2 = new HashLineEditor(new RepoPathGuard(_root), new HashSet<string> { "B.cs" });
        e1.EditFile("A.cs", [new LineEdit(HashLine.Of("a"), null, null, null, "A")]);
        e2.EditFile("B.cs", [new LineEdit(HashLine.Of("b"), null, null, null, "B")]);
        var ctx = Context();
        ctx.AppliedResolutions = [new AppliedResolution(1, "a", ["A.cs"]), new AppliedResolution(2, "b", ["B.cs"] )];
        ctx.ResolutionEditors = new Dictionary<int, HashLineEditor> { [1] = e1, [2] = e2 };
        ctx.ResolutionOutcomes = new Dictionary<int, ResolutionOutcome> { [1] = ResolutionOutcome.Fixed, [2] = ResolutionOutcome.Fixed };
        ctx.ResolutionDetails = new Dictionary<int, string>();

        await new VerifyBuildStage(new ProcessRunner(false), ["build"], TimeSpan.FromSeconds(1), singleCommit: true).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AppliedResolutions);
        Assert.Equal(ResolutionOutcome.VerifyFailed, ctx.ResolutionOutcomes[1]);
        Assert.Equal(ResolutionOutcome.VerifyFailed, ctx.ResolutionOutcomes[2]);
        Assert.Equal("a\n", File.ReadAllText(first));
        Assert.Equal("b\n", File.ReadAllText(second));
    }

    [Fact]
    public async Task Commit_push_downgrades_when_source_ref_is_missing_and_does_not_commit()
    {
        var store = new FakeFindingStore(); var git = new FakeGitOps();
        var ctx = Context(); ctx.PullRequest = ctx.PullRequest! with { SourceRefName = null };
        ctx.ResolvableComments = [Comment(1)];
        ctx.ThreadVerdicts = [new ThreadVerdict(1, TriageVerdict.Actionable, "A.cs:1", "high")];
        ctx.AppliedResolutions = [new AppliedResolution(1, "fixed", ["A.cs"] )];
        ctx.ResolutionOutcomes = new Dictionary<int, ResolutionOutcome> { [1] = ResolutionOutcome.Fixed };

        await new ResolveCommitPushStage(git, store, "PerThread", "bot", "bot@example", null).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(git.Commits);
        Assert.Empty(ctx.AppliedResolutions);
        Assert.Equal(ResolutionOutcome.OutOfScope, ctx.ResolutionOutcomes[1]);
    }

    [Fact]
    public async Task Commit_push_rejects_claim_loss_and_remote_pin_mismatch_before_push()
    {
        var store = new FakeFindingStore(); var git = new FakeGitOps();
        var ctx = Context(); ctx.ResolvableComments = [Comment(1)]; ctx.ThreadVerdicts = [new ThreadVerdict(1, TriageVerdict.Actionable, "A.cs:1", "high")];
        ctx.AppliedResolutions = [new AppliedResolution(1, "fixed", ["A.cs"] )]; ctx.PublishGuard = () => false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResolveCommitPushStage(git, store, "PerThread", "bot", "bot@example").ExecuteAsync(ctx, CancellationToken.None));
        Assert.Empty(git.Commits);

        ctx.PublishGuard = null; git.RemoteTip = "moved";
        await Assert.ThrowsAsync<PrHeadChangedException>(() => new ResolveCommitPushStage(git, store, "PerThread", "bot", "bot@example").ExecuteAsync(ctx, CancellationToken.None));
        Assert.Empty(git.Pushes);
        var audit = Assert.Single(store.ResolveActions);
        Assert.Equal(ResolutionOutcome.PushFailed, audit.Outcome);
        Assert.NotNull(audit.CommitSha);
    }

    [Fact]
    public async Task Commit_push_rejects_claim_loss_at_the_push_boundary()
    {
        var store = new FakeFindingStore(); var git = new FakeGitOps();
        var ctx = Context(); ctx.ResolvableComments = [Comment(1)]; ctx.ThreadVerdicts = [new ThreadVerdict(1, TriageVerdict.Actionable, "A.cs:1", "high")];
        ctx.AppliedResolutions = [new AppliedResolution(1, "fixed", ["A.cs"] )];
        var calls = 0;
        ctx.PublishGuard = () => ++calls < 2; // holds "before resolve commit", lost "at resolve push"

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ResolveCommitPushStage(git, store, "PerThread", "bot", "bot@example").ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(git.Commits);
        Assert.Empty(git.Pushes);
        var audit = Assert.Single(store.ResolveActions);
        Assert.Equal(ResolutionOutcome.PushFailed, audit.Outcome);
    }

    [Fact]
    public async Task Commit_failure_persists_unpublished_outcome_before_failing()
    {
        var store = new FakeFindingStore();
        var git = new FakeGitOps { ThrowOnCommit = new InvalidOperationException("commit failed") };
        var ctx = Context();
        ctx.ResolvableComments = [Comment(1)];
        ctx.ThreadVerdicts = [new ThreadVerdict(1, TriageVerdict.Actionable, "A.cs:1", "high")];
        ctx.AppliedResolutions = [new AppliedResolution(1, "fixed", ["A.cs"])];
        ctx.ResolutionOutcomes = new Dictionary<int, ResolutionOutcome> { [1] = ResolutionOutcome.Fixed };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ResolveCommitPushStage(git, store, "PerThread", "bot", "bot@example")
                .ExecuteAsync(ctx, CancellationToken.None));

        var audit = Assert.Single(store.ResolveActions);
        Assert.Equal(ResolutionOutcome.PushFailed, audit.Outcome);
        Assert.Null(audit.CommitSha);
        Assert.Empty(git.Pushes);
    }

    [Fact]
    public async Task Reply_deduplicates_existing_bot_reply_and_honors_status_opt_in()
    {
        var source = new FakePullRequestSource(); var store = new FakeFindingStore(); var run = Guid.NewGuid();
        var body = CommentFormatter.WithBotPreamble("Fixed in abcdef1 — fix: update. applied");
        var action = new ResolveAction(0, run, 1, TriageVerdict.Actionable, ResolutionOutcome.Fixed,
            "abcdef123", false, DateTimeOffset.UtcNow, body);
        await store.SaveResolveActionsAsync(Key, run, [action], CancellationToken.None);
        var ctx = Context(run);
        ctx.Threads = [new ReviewThread(1, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("bot", "bot", true, body, DateTimeOffset.UtcNow)])];

        await new ReplyCommentsStage(source, store, setFixedStatus: true).ExecuteAsync(ctx, CancellationToken.None);
        Assert.Empty(source.Replies);
        Assert.Contains((1, ReviewThreadStatus.Fixed), source.StatusChanges);
        Assert.True(store.ResolveActions[0].ReplyPosted);
    }

    [Fact]
    public async Task Reply_never_marks_a_human_authored_thread_fixed()
    {
        var source = new FakePullRequestSource(); var store = new FakeFindingStore(); var run = Guid.NewGuid();
        var body = CommentFormatter.WithBotPreamble("Fixed in abcdef1 — fix: update. applied");
        var action = new ResolveAction(0, run, 1, TriageVerdict.Actionable, ResolutionOutcome.Fixed,
            "abcdef123", false, DateTimeOffset.UtcNow, body);
        await store.SaveResolveActionsAsync(Key, run, [action], CancellationToken.None);
        var ctx = Context(run);
        // Human-authored thread (no bot dedupe key): a Fixed outcome must not close it.
        ctx.Threads = [new ReviewThread(1, null, ReviewThreadStatus.Active,
            [new ThreadComment("bot", "bot", true, body, DateTimeOffset.UtcNow)])];

        await new ReplyCommentsStage(source, store, setFixedStatus: true).ExecuteAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain(source.StatusChanges, change => change.Item1 == 1);
        Assert.True(store.ResolveActions[0].ReplyPosted);
    }

    [Fact]
    public async Task Reply_persists_exact_body_before_posting()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var run = Guid.NewGuid();
        await store.SaveResolveActionsAsync(Key, run,
            [new ResolveAction(0, run, 4, TriageVerdict.Question, ResolutionOutcome.Question,
                null, false, DateTimeOffset.UtcNow)], CancellationToken.None);
        var ctx = Context(run);
        ctx.ThreadVerdicts = [new ThreadVerdict(4, TriageVerdict.Question, "please explain", "high",
            Answer: "The API is async.")];

        await new ReplyCommentsStage(source, store, setFixedStatus: false).ExecuteAsync(ctx, CancellationToken.None);

        var posted = Assert.Single(source.Replies);
        Assert.Equal(posted.Text, Assert.Single(store.ResolveActions).ReplyText);
        Assert.True(posted.Text.Contains("The API is async.", StringComparison.Ordinal));
    }


    [Fact]
    public async Task Triage_agent_registers_only_read_context_verdict_and_completion_capabilities()
    {
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("RecordVerdict", new Dictionary<string, object?>
            {
                ["threadId"] = 1, ["verdict"] = "OutOfScope", ["evidence"] = "unclear", ["confidence"] = "low",
            })),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> { ["summary"] = "done" })));

        await new NativeReviewAgent(new FakeChatClientFactory(chat)).RunTriageAsync(
            "triage", new ReviewCollector(), new ContextStore(), _root,
            new HashSet<string>(), null, null, [1], CancellationToken.None);

        var tools = chat.ReceivedOptions.First()?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
        Assert.Contains("RecordVerdict", tools);
        Assert.Contains("TaskDone", tools);
        Assert.Contains("ReadContext", tools);
        Assert.DoesNotContain("EditFile", tools);
        Assert.DoesNotContain("RecordFinding", tools);
        Assert.DoesNotContain("RecordUncertainty", tools);
    }

    [Fact]
    public async Task Triage_stage_fails_closed_when_the_agent_attempts_a_write_tool()
    {
        var file = Path.Combine(_root, "A.cs");
        File.WriteAllText(file, "original\n");
        // The model attempts an edit through the triage pass: no write tool is registered,
        // so the attempt must never reach the filesystem — fail closed, pass continues.
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("EditFile", new Dictionary<string, object?>
            {
                ["path"] = "A.cs",
                ["edits"] = new List<object> { new Dictionary<string, object?> { ["replacement"] = "pwned" } },
            })),
            ScriptedChatClient.FunctionCalls(("RecordVerdict", new Dictionary<string, object?>
            {
                ["threadId"] = 1, ["verdict"] = "OutOfScope", ["evidence"] = "unclear", ["confidence"] = "low",
            })),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> { ["summary"] = "done" })));
        var ctx = Context();
        ctx.ResolvableComments = [Comment(1)];

        await new TriageCommentsStage(new NativeReviewAgent(new FakeChatClientFactory(chat)))
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("original\n", File.ReadAllText(file));
        Assert.Single(ctx.ThreadVerdicts);
    }
    private sealed class ProcessRunner(bool success) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(IReadOnlyList<string> argv, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessRunResult(success ? 0 : 1, "", success ? "" : "failed", false));
    }
}
