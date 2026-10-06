using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Testing;

namespace ReviewForge.Service.Tests;

public sealed class ResolvePipelineTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "reviewforge-resolve-e2e-" + Guid.NewGuid().ToString("N"));
    private static readonly PrKey Key = new("org", "project", "repo", 42);

    public ResolvePipelineTests() => Directory.CreateDirectory(_repo);
    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public async Task Resolve_flow_triages_edits_verifies_commits_pushes_and_replies()
    {
        var file = Path.Combine(_repo, "src", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "return old;\n");
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var git = new FakeGitOps { RepoDir = _repo };
        var chat = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("RecordVerdict", new Dictionary<string, object?>
            {
                ["threadId"] = 7, ["verdict"] = "Actionable", ["evidence"] = "src/A.cs:1",
                ["confidence"] = "high", ["category"] = "bug",
            })),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> { ["summary"] = "triaged" })),
            ScriptedChatClient.FunctionCalls(("EditFile", new Dictionary<string, object?>
            {
                ["path"] = "src/A.cs",
                ["edits"] = new List<object> { new Dictionary<string, object?>
                {
                    ["fromHash"] = HashLine.Of("return old;"), ["toHash"] = null,
                    ["fromLine"] = null, ["toLine"] = null, ["replacement"] = "return new;",
                } },
            })),
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> { ["reviewSummary"] = "updated the return value." })));

        var runId = Guid.NewGuid();
        var ctx = Context(runId);
        ctx.Resolve!.ResolvableComments = [Comment(7)];
        ctx.Threads = [new ReviewThread(7, "k7", ReviewThreadStatus.Active,
            [new ThreadComment("human", "Human", false, "Please update this", DateTimeOffset.UtcNow)],
            new ThreadAnchor("src/A.cs", 1, 1))];
        ctx.ChangedFileManifest = [new ChangedFile("src/A.cs", ChangedFileType.Edit)];

        var agent = new NativeReviewAgent(new FakeChatClientFactory(chat));
        var process = new SuccessfulProcessRunner();
        await new ReviewPipeline([
            new TriageCommentsStage(agent),
            new PlanFixesStage(10, 10),
            new ApplyFixesStage(agent, 4),
            new VerifyBuildStage(process, ["build"], TimeSpan.FromSeconds(1), singleCommit: false),
            new ResolveCommitPushStage(git, store, "Single", "ReviewForge", "bot@example.test"),
            new ReplyCommentsStage(source, store, setFixedStatus: true),
        ], NullLogger<ReviewPipeline>.Instance).RunAsync(ctx, CancellationToken.None);

        var action = Assert.Single(store.ResolveActions);
        Assert.Equal(ResolutionOutcome.Fixed, action.Outcome);
        Assert.Equal("fake-0000000000000000000000000000000000000001", action.CommitSha);
        Assert.True(action.ReplyPosted);
        Assert.Contains((7, ReviewThreadStatus.Fixed), source.StatusChanges);
        Assert.Contains("Commit:", Assert.Single(source.GeneralComments));
        Assert.Equal(("feature/test", "head-sha"), Assert.Single(git.Pushes));
        Assert.Equal("return new;\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Resolve_reply_reconciles_durable_action_after_crash_before_reply()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var runId = Guid.NewGuid();
        const string sha = "fake-0000000000000000000000000000000000000001";
        await store.SaveResolveActionsAsync(Key, runId,
            [new ResolveAction(0, runId, 7, TriageVerdict.Actionable, ResolutionOutcome.Fixed, sha, false, DateTimeOffset.UtcNow)],
            CancellationToken.None);
        var ctx = Context(runId);
        ctx.Resolve!.ThreadVerdicts = [new ThreadVerdict(7, TriageVerdict.Actionable, "src/A.cs:1", "high")];
        ctx.Resolve!.AppliedResolutions = [new AppliedResolution(7, "updated the return value.", ["src/A.cs"], sha, "fix(resolve): update return value")];
        var body = CommentFormatter.WithBotPreamble($"Fixed in {sha[..7]} — fix(resolve): update return value. ");
        ctx.Threads = [new ReviewThread(7, null, ReviewThreadStatus.Active,
            [new ThreadComment("bot", "reviewforge", true, body, DateTimeOffset.UtcNow)])];

        await new ReplyCommentsStage(source, store, setFixedStatus: true).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.Replies);
        Assert.DoesNotContain(source.StatusChanges, change => change.Item1 == 7);
        Assert.True(Assert.Single(store.ResolveActions).ReplyPosted);
    }

    private ReviewContext Context(Guid runId)
    {
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow, runId)
        {
            RepoDir = _repo,
            RunKind = RunKind.Resolve,
            Resolve = new ResolveState(),
        };
        ctx.PullRequest = sourcePr;
        return ctx;
    }

    private static PullRequest sourcePr => new(42, "Resolve", null, "head-sha", "base-sha", "https://clone", false,
        "creator-1", "PR Author", "refs/heads/feature/test");

    private static ResolvableComment Comment(int id)
        => new(id, new ThreadAnchor("src/A.cs", 1, 1), "creator-1", "PR Author", [], "Please update this", true);

    private sealed class SuccessfulProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(IReadOnlyList<string> argv, string? workingDirectory, TimeSpan timeout,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessRunResult(0, "verified", "", false));
    }
}
