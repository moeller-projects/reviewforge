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
public class CoverageGapTests
{
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
            new PrepareRepositoryStage(new RepoCheckoutPool(new FakeGitOps(), Path.GetTempPath())),
            new ClassifyRunStage(source),
            new EnrichContextStage(null, NullLogger<EnrichContextStage>.Instance),
            new ExecuteReasoningStage(agent),
            new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance),
            new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance),
            new PublishFindingsStage(source, NullLogger<PublishFindingsStage>.Instance),
            new PersistRunStage(store),
        ];

        Assert.Equal(
            [
                "fetch-pr-context", "review-gate", "prepare-repository", "classify-run", "enrich-context",
                "execute-reasoning", "validate-findings", "triage-threads", "publish-findings", "persist-run"
            ],
            stages.Select(s => s.Name));
    }

    [Fact]
    public async Task Triage_flags_unanswered_threads_without_writing()
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(9, "k", ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", false, "please explain", DateTimeOffset.UtcNow)]));

        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow)
        {
            Threads = source.Threads,
            Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []},
            AcceptedFindings = [],
        };

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal([9], ctx.UnansweredThreads);
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
                RepoDir = repoDir,
                Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []},
            };

            var stage = new ValidateFindingsStage(
                NullLogger<ValidateFindingsStage>.Instance,
                lineReader: _ => throw new IOException("disk gone"));
            await stage.ExecuteAsync(ctx, CancellationToken.None);

            Assert.Empty(ctx.AcceptedFindings);
        }
        finally
        {
            Directory.Delete(repoDir, recursive: true);
        }
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
        protected override string[] ReadAllLines(string path) => throw new IOException("io failed");
    }
}