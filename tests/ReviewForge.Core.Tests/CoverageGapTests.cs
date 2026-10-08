using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

/// <summary>Tests pinned to specific pipeline behaviors that the happy-path suite does not reach.</summary>
public class CoverageGapTests : IDisposable
{
    // The pool constructor creates checkouts/ + mirror/; keep it out of the shared temp root.
    private readonly string _PoolRoot = Path.Combine(Path.GetTempPath(), "reviewforge-coverage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_PoolRoot))
        {
            Directory.Delete(_PoolRoot, recursive: true);
        }
    }

    [Fact]
    public void All_stages_have_stable_names()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var agent = new NativeReviewAgent(new FakeChatClientFactory(new ScriptedChatClient()));

        IReviewStage[] stages =
        [
            new FetchPrContextStage(source, store),
            new ReviewGateStage(),
            new PrepareRepositoryStage(new RepoCheckoutPool(new FakeGitOps(), new FakeWorkspaceFs(), _PoolRoot), NullLogger<PrepareRepositoryStage>.Instance),
            new ClassifyRunStage(source),
            new EnrichContextStage(null, NullLogger<EnrichContextStage>.Instance),
            new ExecuteReasoningStage(agent, 200_000, 40_000),
            new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance),
            new BeginRunStage(store),
            new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance),
            new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance),
            new PersistRunStage(store),
        ];

        Assert.Equal(
            [
                "fetch-pr-context", "review-gate", "prepare-repository", "classify-run", "enrich-context",
                "execute-reasoning", "validate-findings", "begin-run", "triage-threads", "publish-findings", "persist-run"
            ],
            stages.Select(s => s.Name));
    }

    [Fact]
    public async Task Persist_draft_saves_only_current_verified_findings_with_full_payload()
    {
        var store = new FakeFindingStore
        {
            LastRun = new PriorRun(
                new PrKey("o", "p", "r", 1), "old-head", DateTimeOffset.UtcNow.AddDays(-1), ["prior-key"],
                [new StoredFinding("prior-key", "r", "low", "prior", null, null, null)]),
        };
        var finding = new RichFinding
        {
            RuleId = "r", Title = "finding", Severity = "high", Category = "bug", Description = "description",
            Anchor = new FindingAnchor("src/file.cs", 5, 5), DedupeKey = "current-key",
        };
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            RunKind = RunKind.ReviewDraft,
            Fetch = new FetchOutcome
            {
                PullRequest = new FakePullRequestSource().Pr,
                Threads =
                [
                    new ReviewThread(1, null, ReviewThreadStatus.Active,
                        [new ThreadComment("u", "user", false, "comment", DateTimeOffset.UtcNow)])
                ],
            },
            Classification = new Classification {Kind = ReviewKind.Full},
            Validation = new ValidationOutcome
            {
                AcceptedFindings = [finding, finding with {DedupeKey = null, Anchor = null}],
            },
        };

        var stage = new PersistDraftRunStage(store);
        Assert.Equal("persist-draft-run", stage.Name);
        await stage.ExecuteAsync(ctx, CancellationToken.None);

        var saved = Assert.Single(store.Runs);
        Assert.Equal(nameof(RunKind.ReviewDraft), saved.Pipeline);
        var row = Assert.Single(saved.Findings);
        Assert.Equal("current-key", row.DedupeKey);
        Assert.NotNull(row.FindingJson);
        Assert.Null(row.ThreadId);
        Assert.Equal(DateTimeOffset.UtcNow.Date, saved.LastObservedCommentAt!.Value.Date);
    }

    [Fact]
    public async Task Begin_draft_persists_running_shell_after_gate()
    {
        var store = new FakeFindingStore();
        var source = new FakePullRequestSource();
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            RunKind = RunKind.ReviewDraft,
            Fetch = new FetchOutcome {PullRequest = source.Pr},
        };
        var stage = new BeginDraftRunStage(store);
        Assert.Equal("begin-draft-run", stage.Name);

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        var shell = Assert.Single(store.Runs);
        Assert.Equal(ctx.RunId, shell.Id);
        Assert.Equal("head-sha", shell.HeadSha);
        Assert.Equal(nameof(RunKind.ReviewDraft), shell.Pipeline);
        Assert.Null(shell.CompletedAt);
        Assert.False(shell.Success);
        Assert.Empty(shell.Findings);
    }

    [Fact]
    public async Task Triage_flags_unanswered_threads_without_writing()
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(9, "k", ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", false, "please explain", DateTimeOffset.UtcNow)]));

        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            Fetch = new FetchOutcome
            {
                PullRequest = source.Pr,
                Threads = source.Threads,
            },
            Reasoning = new ReasoningOutcome {Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []}},
            Validation = new ValidationOutcome {AcceptedFindings = []},
        };

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal([9], ctx.Triage.UnansweredThreads);
        Assert.Empty(source.Replies); // Op.None writes nothing
        Assert.Empty(source.StatusChanges);
    }

    [Fact]
    public async Task Validate_downgrades_when_file_unreadable()
    {
        var finding = new RichFinding
        {
            RuleId = "r", Title = "t", Severity = "low", Category = "style", Description = "d",
            Snippet = "x", Anchor = new FindingAnchor("src/A.cs", 1, 1), DedupeKey = "k",
        };
        var repoDir = Path.Combine(Path.GetTempPath(), "reviewforge-iofail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repoDir, "src"));
        File.WriteAllText(Path.Combine(repoDir, "src", "A.cs"), "x\n");
        try
        {
            var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
            {
                Repository = new RepoPreparation {RepoDir = repoDir},
                Reasoning = new ReasoningOutcome {Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []}},
            };

            var stage = new ValidateFindingsStage(
                NullLogger<ValidateFindingsStage>.Instance,
                lineReader: _ => throw new IOException("disk gone"));
            await stage.ExecuteAsync(ctx, CancellationToken.None);

            Assert.Empty(ctx.Validation.AcceptedFindings);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Fact]
    public async Task Enrich_skips_gracefully_when_repo_not_prepared()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            Repository = new RepoPreparation {RepoDir = null},
        };

        await new EnrichContextStage(new FakeEnricher("graph"), NullLogger<EnrichContextStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.Reasoning.ContextStore.Names);
    }

    [Fact]
    public void RequireRepoDir_throws_when_null()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => ctx.RequireRepoDir());
    }

    [Fact]
    public void RequirePullRequest_throws_when_unset()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.RequirePullRequest());
        Assert.Contains("PullRequest", ex.Message);
    }

    [Fact]
    public void RequireResult_throws_when_unset()
    {
        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() => ctx.RequireResult());
        Assert.Contains("Result", ex.Message);
    }
}

public class RepoReadToolsGapTests : IDisposable
{
    private readonly string _Root;

    public RepoReadToolsGapTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-gaps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_Root);
        File.WriteAllLines(Path.Combine(_Root, "many.txt"), Enumerable.Range(1, 250).Select(i => $"match line {i}"));
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    [Fact]
    public void List_and_grep_on_denied_paths_are_refused()
    {
        var tools = new RepoReadTools(_Root);
        Assert.Contains("denied", tools.List(".git"));
        Assert.Contains("denied", tools.Grep("x", path: ".git"));
    }

    [Fact]
    public void Grep_stops_at_match_cap()
    {
        var result = new RepoReadTools(_Root).Grep("match line");
        Assert.Contains("match cap reached", result);
    }

    [Fact]
    public void Io_failures_are_reported_not_thrown()
    {
        var tools = new FailingIoTools(_Root);
        Assert.Contains("unreadable", tools.ReadFile("many.txt"));
        Assert.Equal("no matches", tools.Grep("match")); // failing files are skipped
    }

    private sealed class FailingIoTools(string root) : RepoReadTools(root)
    {
        protected override IEnumerable<string> ReadLinesSafe(string file) => throw new IOException("io failed");
    }
}