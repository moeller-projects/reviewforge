using Microsoft.Extensions.Logging.Abstractions;
using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Pipeline;
using ReviewForge.Core.Pipeline.Stages;
using ReviewForge.Core.Ports;
using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Workspaces;
using ReviewForge.Testing;
using Xunit;

namespace ReviewForge.Core.Tests;

public class ReviewPipelineTests
{
    [Fact]
    public async Task Stages_run_in_order()
    {
        var log = new List<string>();
        var pipeline = new ReviewPipeline(
            [new RecordingStage("a", log), new RecordingStage("b", log)],
            NullLogger<ReviewPipeline>.Instance);

        await pipeline.RunAsync(new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public async Task Terminated_context_skips_remaining_stages()
    {
        var log = new List<string>();
        var pipeline = new ReviewPipeline(
            [
                new RecordingStage("a", log, ctx => ctx.Terminate("done here")),
                new RecordingStage("b", log),
            ],
            NullLogger<ReviewPipeline>.Instance);

        var ctx = await pipeline.RunAsync(new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow), CancellationToken.None);
        Assert.Equal(["a"], log);
        Assert.True(ctx.Terminated);
        Assert.Equal("done here", ctx.TerminationReason);
    }

    [Fact]
    public async Task Failing_stage_faults_the_run()
    {
        var pipeline = new ReviewPipeline(
            [new RecordingStage("boom", [], _ => throw new InvalidOperationException("stage failed"))],
            NullLogger<ReviewPipeline>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.RunAsync(new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow), CancellationToken.None));
    }

    [Fact]
    public async Task Head_scope_opens_after_stage_sets_pull_request()
    {
        var log = new List<string>();
        var pipeline = new ReviewPipeline(
            [
                new RecordingStage("fetch", log, ctx => ctx.PullRequest = new PullRequest(1, "t", null, "head-sha", "base", "url", false)),
                new RecordingStage("after", log),
            ],
            NullLogger<ReviewPipeline>.Instance);

        var ctx = new ReviewContext(new PrKey("o", "p", "r", 1), DateTimeOffset.UtcNow);
        await pipeline.RunAsync(ctx, CancellationToken.None);

        Assert.Equal(["fetch", "after"], log);
        Assert.Equal("head-sha", ctx.PullRequest!.SourceCommitSha);
    }

    private sealed class RecordingStage(
        string name, List<string> log, Action<ReviewContext>? act = null, int? order = null) : IReviewStage
    {
        private static int _NextOrder;

        public int Order { get; } = order ?? Interlocked.Increment(ref _NextOrder);

        public string Name => name;

        public Task ExecuteAsync(ReviewContext ctx, CancellationToken ct)
        {
            log.Add(name);
            act?.Invoke(ctx);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Pipeline_throws_on_out_of_order_stages()
    {
        var log = new List<string>();

        var ex = Assert.Throws<InvalidOperationException>(() => _ = new ReviewPipeline(
            [new RecordingStage("later", log, order: 30), new RecordingStage("earlier", log, order: 10)],
            NullLogger<ReviewPipeline>.Instance));

        Assert.Contains("stage ordering violation", ex.Message);
        Assert.Contains("earlier", ex.Message);
    }

    [Fact]
    public void Pipeline_throws_on_duplicate_orders()
    {
        var log = new List<string>();

        Assert.Throws<InvalidOperationException>(() => _ = new ReviewPipeline(
            [new RecordingStage("a", log, order: 10), new RecordingStage("b", log, order: 10)],
            NullLogger<ReviewPipeline>.Instance));
    }
}

public class StageTests : IDisposable
{
    private static readonly PrKey Key = new("org", "proj", "repo", 7);
    private readonly string _RepoDir;

    public StageTests()
    {
        _RepoDir = Path.Combine(Path.GetTempPath(), "reviewforge-stages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_RepoDir, "src"));
        File.WriteAllLines(Path.Combine(_RepoDir, "src", "A.cs"), ["line one", "bad code here", "line three"]);
    }

    public void Dispose() => Directory.Delete(_RepoDir, recursive: true);

    private ReviewContext Ctx(FakePullRequestSource? source = null)
    {
        var src = source ?? new FakePullRequestSource();
        if (source is null)
            src.ChangedFiles.Add(new ChangedFile("src/A.cs", ChangedFileType.Edit));
        return new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = src.Pr,
            CurrentUser = src.User,
            Threads = src.Threads,
            ChangedFileManifest = src.ChangedFiles,
            RepoDir = _RepoDir,
        };
    }

    private sealed class FaultingPullRequestSource : FakePullRequestSource
    {
        public TaskCompletionSource SiblingStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSibling { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
            => Task.FromException<PullRequest>(new InvalidOperationException("pull request failed"));

        public override async Task<IReadOnlyList<WorkItem>> GetLinkedWorkItemsAsync(
            PrKey pr,
            CancellationToken ct)
        {
            SiblingStarted.TrySetResult();
            await ReleaseSibling.Task.WaitAsync(ct);
            return [];
        }
    }

    [Fact]
    public void ChangedFiles_is_cached_until_manifest_reassigned()
    {
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        ctx.ChangedFileManifest = [new ChangedFile("a.cs", ChangedFileType.Edit)];

        var first = ctx.ChangedFiles;
        var second = ctx.ChangedFiles;
        Assert.Same(first, second); // cached projection

        ctx.ChangedFileManifest = [new ChangedFile("b.cs", ChangedFileType.Edit)];
        var third = ctx.ChangedFiles;
        Assert.NotSame(first, third);
        Assert.Equal(["b.cs"], third);
    }

    [Fact]
    public async Task Fetch_populates_context()
    {
        var source = new FakePullRequestSource();
        source.WorkItems.Add(new WorkItem(1, "wi", "Bug", null, null, "Active"));
        source.ChangedFiles.Add(new ChangedFile("src/A.cs", ChangedFileType.Edit));
        var store = new FakeFindingStore {LastRun = new PriorRun(Key, "old", DateTimeOffset.UtcNow.AddDays(-1), ["k"])};

        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow);
        await new FetchPrContextStage(source, store).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(source.Pr, ctx.PullRequest);
        Assert.Single(ctx.WorkItems);
        Assert.Single(ctx.ChangedFiles);
        Assert.Equal(source.User, ctx.CurrentUser);
        Assert.Equal(store.LastRun, ctx.PriorRun);
    }

    [Fact]
    public async Task Fetch_observes_started_siblings_when_pull_request_fails()
    {
        var source = new FaultingPullRequestSource();
        var execution = new FetchPrContextStage(source, new FakeFindingStore())
            .ExecuteAsync(new ReviewContext(Key, DateTimeOffset.UtcNow), CancellationToken.None);

        await source.SiblingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            Assert.False(execution.IsCompleted);
        }
        finally
        {
            source.ReleaseSibling.TrySetResult();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);
    }

    [Fact]
    public async Task Gate_terminates_on_skip()
    {
        var source = new FakePullRequestSource();
        source.Pr = source.Pr with {IsDraft = true};
        var ctx = Ctx(source);

        await new ReviewGateStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(ctx.Terminated);
        Assert.Equal("PR is a draft", ctx.TerminationReason);
    }

    [Fact]
    public async Task Prepare_clones_checks_out_and_indexes_diff()
    {
        var git = new FakeGitOps
        {
            RepoDir = _RepoDir,
            Diff = "+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n",
        };
        var ctx = Ctx();

        await new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), Path.GetTempPath(), "pat"), NullLogger<PrepareRepositoryStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["head-sha"], git.Checkouts);
        Assert.Equal([("base-sha", "head-sha")], git.EnsuredCommits);
        Assert.True(ctx.Diff!.Contains("src/A.cs", 2));
        Assert.False(ctx.Diff.Contains("src/A.cs", 1));
    }

    [Fact]
    public async Task Prepare_scope_check_ignores_excluded_files()
    {
        var source = new FakePullRequestSource();
        source.ChangedFiles.Add(new ChangedFile("src/A.cs", ChangedFileType.Edit));
        source.ChangedFiles.Add(new ChangedFile("package-lock.json", ChangedFileType.Edit));
        var git = new FakeGitOps
        {
            RepoDir = _RepoDir,
            Diff = "+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n", // no package-lock.json (excluded at the git layer)
        };
        var ctx = Ctx(source);

        await new PrepareRepositoryStage(
            new RepoCheckoutPool(git, new FakeWorkspaceFs(), Path.GetTempPath(), "pat"),
            NullLogger<PrepareRepositoryStage>.Instance,
            DiffBudget.Default).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Contains("src/A.cs", ctx.ReviewableFiles!);
        Assert.DoesNotContain("package-lock.json", ctx.ReviewableFiles!);
    }

    [Fact]
    public async Task Classify_refetches_threads_and_sets_kind()
    {
        var source = new FakePullRequestSource();
        source.Threads.Add(new ReviewThread(1, "k", ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", false, "?", DateTimeOffset.UtcNow)]));
        var ctx = Ctx(source);
        ctx.PriorRun = new PriorRun(Key, "s", DateTimeOffset.UtcNow, []);

        await new ClassifyRunStage(source).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(1, source.ThreadFetches);
        Assert.Equal(ReviewKind.FollowUp, ctx.Kind);
        Assert.Single(ctx.PendingReplies);
    }

    [Fact]
    public async Task Enrich_stores_payload_skips_null_and_swallows_failures()
    {
        var logger = NullLogger<EnrichContextStage>.Instance;

        var noEnricher = Ctx();
        await new EnrichContextStage(null, logger).ExecuteAsync(noEnricher, CancellationToken.None);
        Assert.Empty(noEnricher.ContextStore.Names);

        var withPayload = Ctx();
        await new EnrichContextStage(new FakeEnricher("graph"), logger).ExecuteAsync(withPayload, CancellationToken.None);
        Assert.Equal("graph", withPayload.ContextStore.Read("crg"));

        var empty = Ctx();
        await new EnrichContextStage(new FakeEnricher(" "), logger).ExecuteAsync(empty, CancellationToken.None);
        Assert.Empty(empty.ContextStore.Names);

        var throwing = Ctx();
        await new EnrichContextStage(new FakeEnricher(throws: true), logger).ExecuteAsync(throwing, CancellationToken.None);
        Assert.Empty(throwing.ContextStore.Names);
    }

    [Fact]
    public async Task ExecuteReasoning_builds_prompt_and_collects_result()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
        var ctx = Ctx();
        ctx.PriorRun = new PriorRun(Key, "s", DateTimeOffset.UtcNow, ["known-key"]);

        await new ExecuteReasoningStage(agent).ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(ctx.Result);
        Assert.True(ctx.Collector.IsKnown("known-key"));
        var prompt = script.Received[0].Last().Text;
        Assert.Contains("full code review", prompt);
    }

    [Fact]
    public async Task ExecuteReasoning_assigns_and_deduplicates_automatic_homoglyph_findings()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
        var ctx = Ctx();
        ctx.DiffText = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -0,0 +1,1 @@
            +var fileNаme = value;
            """;

        await new ExecuteReasoningStage(new NativeReviewAgent(new FakeChatClientFactory(script)))
            .ExecuteAsync(ctx, CancellationToken.None);

        var finding = Assert.Single(ctx.Collector.Findings);
        Assert.NotNull(finding.DedupeKey);
        Assert.Equal(
            DedupeKey.Compute(finding.RuleId, finding.Anchor!.FilePath, finding.Snippet),
            finding.DedupeKey);
    }

    [Fact]
    public async Task ExecuteReasoning_accepts_homoglyph_regression_of_resolved_thread()
    {
        var script = new ScriptedChatClient(
            ScriptedChatClient.FunctionCalls(("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
        var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -0,0 +1,1 @@
            +var fileNаme = value;
            """;

        // Run 1: establishes the finding and its dedupe key.
        var first = Ctx();
        first.DiffText = diff;
        await new ExecuteReasoningStage(agent).ExecuteAsync(first, CancellationToken.None);
        var key = Assert.Single(first.Collector.Findings).DedupeKey!;

        // Run 2: same diff; prior run knows the key; the live thread is Fixed → the
        // verbatim re-detection resurfaces as a regression instead of staying deduped.
        var regressed = Ctx();
        regressed.DiffText = diff;
        regressed.PriorRun = new PriorRun(Key, "s", DateTimeOffset.UtcNow, [key]);
        regressed.Threads =
        [
            new ReviewThread(7, key, ReviewThreadStatus.Fixed,
                [new ThreadComment("b", "bot", true, "finding", DateTimeOffset.UtcNow)]),
        ];

        await new ExecuteReasoningStage(agent).ExecuteAsync(regressed, CancellationToken.None);

        var finding = Assert.Single(regressed.Collector.Findings);
        Assert.True(finding.IsRegression);
        Assert.Contains(key, regressed.Collector.RegressedKeys);
        Assert.Empty(regressed.Collector.RedetectedKeys);
        Assert.Contains(key, regressed.ResolvedKeys);

        // Run 3: same diff but the thread is still Active → plain redetection, silent.
        var active = Ctx();
        active.DiffText = diff;
        active.PriorRun = new PriorRun(Key, "s", DateTimeOffset.UtcNow, [key]);
        active.Threads =
        [
            new ReviewThread(7, key, ReviewThreadStatus.Active,
                [new ThreadComment("b", "bot", true, "finding", DateTimeOffset.UtcNow)]),
        ];

        await new ExecuteReasoningStage(agent).ExecuteAsync(active, CancellationToken.None);

        Assert.Empty(active.Collector.Findings);
        Assert.Contains(key, active.Collector.RedetectedKeys);
        Assert.Empty(active.Collector.RegressedKeys);
    }

    [Fact]
    public async Task ExecuteReasoning_streams_findings_to_per_run_jsonl()
    {
        var findingsDir = Path.Combine(Path.GetTempPath(), "reviewforge-findings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(findingsDir);
        try
        {
            var script = new ScriptedChatClient(
                ScriptedChatClient.FunctionCalls(
                    ("RecordFinding", new Dictionary<string, object?>
                    {
                        ["ruleId"] = "csharp.null-deref", ["title"] = "x may be null", ["severity"] = "high",
                        ["category"] = "bug", ["description"] = "deref", ["snippet"] = "bad code here",
                        ["filePath"] = "src/A.cs", ["startLine"] = 2,
                    }),
                    ("TaskDone", new Dictionary<string, object?> {["reviewSummary"] = "ok"})));
            var agent = new NativeReviewAgent(new FakeChatClientFactory(script));
            var ctx = Ctx();

            await new ExecuteReasoningStage(agent, findingsDir).ExecuteAsync(ctx, CancellationToken.None);

            var file = Path.Combine(findingsDir, $"{ctx.RunId:N}.jsonl");
            Assert.True(File.Exists(file));
            var line = Assert.Single(File.ReadLines(file));
            Assert.Contains(ctx.RunId.ToString(), line);
        }
        finally
        {
            Directory.Delete(findingsDir, recursive: true);
        }
    }

    private static RichFinding FindingOnLine(int line, string snippet = "bad code here") => new()
    {
        RuleId = "r", Title = "t", Severity = "high", Category = "bug", Description = "d",
        Snippet = snippet, Anchor = new FindingAnchor("src/A.cs", line, line), DedupeKey = "k" + line,
    };

    [Fact]
    public async Task Validate_accepts_only_verified_changed_lines()
    {
        var verified = FindingOnLine(2);
        var reanchored = FindingOnLine(1); // snippet actually on line 2
        var outsideDiff = FindingOnLine(2, "line one"); // verified but not in diff
        var gone = FindingOnLine(2, "vanished snippet");
        var prLevel = FindingOnLine(2);
        prLevel.Anchor = null;
        prLevel.DedupeKey = "k-pr";

        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse("+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n");
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative(),
            Findings = [verified, reanchored, outsideDiff, gone, prLevel],
            Uncertainties = [],
        };

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, ctx.AcceptedFindings.Count);
        Assert.False(verified.AnchorDowngraded);
        Assert.False(reanchored.AnchorDowngraded);
        Assert.Equal(2, reanchored.Anchor!.StartLine);
        Assert.DoesNotContain(outsideDiff, ctx.AcceptedFindings);
        Assert.DoesNotContain(gone, ctx.AcceptedFindings);
    }

    [Fact]
    public async Task Validate_weak_snippet_downgrades_but_keeps_finding()
    {
        var finding = FindingOnLine(2, "}");
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse("+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        var accepted = Assert.Single(ctx.AcceptedFindings);
        Assert.True(accepted.AnchorDowngraded);
        Assert.Equal(2, accepted.Anchor!.StartLine);
    }

    [Fact]
    public async Task Validate_weak_snippet_outside_diff_still_rejected()
    {
        var finding = FindingOnLine(3, "}");
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse("+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AcceptedFindings);
    }

    [Fact]
    public async Task Validate_rejects_findings_for_truncated_files()
    {
        var finding = FindingOnLine(1);
        finding.Anchor = new FindingAnchor("src/Large.cs", 1, 1);
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse(
            "diff --git a/src/Large.cs b/src/Large.cs\n" +
            "--- a/src/Large.cs\n+++ b/src/Large.cs\n" +
            "…[file diff skipped — 300 bytes exceeds the per-file budget; use repo_read_file]\n");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AcceptedFindings);
    }

    [Fact]
    public async Task Validate_reads_each_file_once()
    {
        var reads = new List<string>();
        var stage = new ValidateFindingsStage(
            NullLogger<ValidateFindingsStage>.Instance,
            lineReader: path =>
            {
                reads.Add(path);
                return File.ReadAllLines(path);
            });

        var f1 = FindingOnLine(2);
        f1.DedupeKey = "k1";
        var f2 = FindingOnLine(2);
        f2.DedupeKey = "k2";
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse("+++ b/src/A.cs\n@@ -1,1 +2,1 @@\n+bad code here\n");
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative(),
            Findings = [f1, f2],
            Uncertainties = [],
        };

        await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(reads);
        Assert.Equal(2, ctx.AcceptedFindings.Count);
    }

    [Fact]
    public async Task Validate_rejects_when_file_missing()
    {
        var finding = FindingOnLine(1);
        finding.Anchor = new FindingAnchor("missing.cs", 1, 1);
        var ctx = Ctx();
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);
        Assert.Empty(ctx.AcceptedFindings);
    }

    [Fact]
    public async Task Validate_rejects_sibling_directory_prefix_escape()
    {
        var sibling = _RepoDir + "-sibling";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "evil.cs"), "bad code here");
        try
        {
            var finding = FindingOnLine(1, "bad code here");
            finding.Anchor = new FindingAnchor($"../{Path.GetFileName(sibling)}/evil.cs", 1, 1);
            var ctx = Ctx();
            ctx.Result = new ReviewResult
            {
                Narrative = new ReviewNarrative(),
                Findings = [finding],
                Uncertainties = [],
            };

            await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None);

            Assert.Empty(ctx.AcceptedFindings);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_rejects_when_file_is_not_in_diff()
    {
        var finding = FindingOnLine(2);
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse(
            "+++ b/src/Other.cs\n@@ -0,0 +1,1 @@\n+changed\n");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.AcceptedFindings);
    }

    [Fact]
    public async Task Prepare_rejects_provider_scope_mismatch()
    {
        var git = new FakeGitOps
        {
            Diff = "+++ b/src/Other.cs\n@@ -0,0 +1,1 @@\n+changed\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("src/A.cs", ChangedFileType.Edit)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_accepts_manifest_with_binary_file()
    {
        var git = new FakeGitOps
        {
            Diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1,1 @@\n+changed\n" +
                   "diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("a.cs", ChangedFileType.Edit), new ChangedFile("logo.png", ChangedFileType.Edit)],
        };

        await new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["a.cs"], ctx.ReviewableFiles!.Order());
    }

    [Fact]
    public async Task Prepare_accepts_content_free_rename()
    {
        var git = new FakeGitOps
        {
            Diff = "diff --git a/a.cs b/b.cs\nsimilarity index 100%\nrename from a.cs\nrename to b.cs\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("b.cs", ChangedFileType.Rename)],
        };

        await new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.ReviewableFiles!);
    }

    [Fact]
    public async Task Prepare_accepts_quoted_diff_paths_end_to_end()
    {
        var git = new FakeGitOps
        {
            Diff = "diff --git \"a/caf\\303\\251.cs\" \"b/caf\\303\\251.cs\"\n" +
                   "--- \"a/caf\\303\\251.cs\"\n+++ \"b/caf\\303\\251.cs\"\n@@ -0,0 +1,1 @@\n+x\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("café.cs", ChangedFileType.Edit)],
        };

        await new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["café.cs"], ctx.ReviewableFiles!.Order());
    }

    [Fact]
    public async Task Prepare_excludes_binary_manifest_file_without_failing()
    {
        var git = new FakeGitOps
        {
            Diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1,1 @@\n+x\n" +
                   "diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("a.cs", ChangedFileType.Edit), new ChangedFile("logo.png", ChangedFileType.Edit)],
        };

        await new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["a.cs"], ctx.ReviewableFiles!.Order());
    }

    [Fact]
    public async Task Prepare_still_throws_when_diff_has_unaccounted_text_file()
    {
        var git = new FakeGitOps
        {
            Diff = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1,1 @@\n+x\n" +
                   "diff --git a/c.cs b/c.cs\n--- a/c.cs\n+++ b/c.cs\n@@ -0,0 +1,1 @@\n+y\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("a.cs", ChangedFileType.Edit)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_throws_on_manifest_orphan()
    {
        var git = new FakeGitOps
        {
            Diff = "+++ b/a.cs\n@@ -0,0 +1,1 @@\n+x\n",
        };
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = new PullRequest(7, "title", null, "head", "base", "url", false),
            ChangedFileManifest = [new ChangedFile("a.cs", ChangedFileType.Edit), new ChangedFile("ghost.cs", ChangedFileType.Edit)],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PrepareRepositoryStage(new RepoCheckoutPool(git, new FakeWorkspaceFs(), _RepoDir), NullLogger<PrepareRepositoryStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("ghost.cs", ex.Message);
    }

    [Fact]
    public async Task Triage_applies_operations_and_flags_unanswered()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(1, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("u", "human", false, "reply", t0)]));
        source.Threads.Add(new ReviewThread(2, "stale", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0)]));

        var ctx = Ctx(source);
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ThreadActions = [new ThreadAction(1, ThreadActionKind.Resolve, "fixed")]},
            Findings = [],
            Uncertainties = [],
        };
        ctx.AcceptedFindings = [];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Contains(source.Replies, r => r.ThreadId == 1 && r.Text == CommentFormatter.WithBotPreamble("fixed"));
        Assert.Contains(source.StatusChanges, s => s.ThreadId == 1 && s.Status == ReviewThreadStatus.Fixed);
        Assert.Contains(source.StatusChanges, s => s.ThreadId == 2 && s.Status == ReviewThreadStatus.Fixed); // stale auto-resolved
        Assert.Empty(ctx.UnansweredThreads);
    }

    [Fact]
    public async Task Triage_resolves_thread_when_key_only_exists_in_prior_run()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(2, "k", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0)]));

        var ctx = Ctx(source);
        ctx.PriorRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, ["k"]);
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []};
        ctx.AcceptedFindings = [];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Contains(source.StatusChanges, s => s.ThreadId == 2 && s.Status == ReviewThreadStatus.Fixed);
        Assert.Contains(source.Replies, r => r.ThreadId == 2 && r.Text == CommentFormatter.WithBotPreamble(
            "Resolved: this finding no longer reproduces in the latest iteration."));
    }

    [Fact]
    public async Task Triage_keeps_thread_when_finding_redetected()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(2, "k", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0)]));

        var ctx = Ctx(source);
        ctx.Collector.MarkRedetected("k");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []};
        ctx.AcceptedFindings = [];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.StatusChanges);
        Assert.Empty(source.Replies);
    }

    [Fact]
    public async Task Triage_reopens_fixed_thread_when_finding_regressed()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(7, "k", ReviewThreadStatus.Fixed,
            [new ThreadComment("b", "bot", true, "finding", t0)]));

        var regressed = FindingOnLine(2);
        regressed.DedupeKey = "k";
        regressed.IsRegression = true;

        var ctx = Ctx(source);
        ctx.Collector.MarkRegressed("k");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [regressed], Uncertainties = []};
        ctx.AcceptedFindings = [regressed];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        var op = Assert.Single(ctx.TriagePlan.Where(o => o.Op != TriageOp.None));
        Assert.Equal(TriageOp.Reopen, op.Op);
        Assert.Contains(source.Replies, r => r.ThreadId == 7 && r.Text.Contains("Regressed in head-sh"));
        Assert.Contains(source.StatusChanges, s => s.ThreadId == 7 && s.Status == ReviewThreadStatus.Active);
    }

    [Fact]
    public async Task Publish_suppresses_reposted_regression_and_stamps_reopened_thread_id()
    {
        // F1 surfacing (P1-11): the regressed finding is NOT re-posted as a new thread —
        // triage reopens thread 7 instead — and the reopened thread id is re-stamped for
        // persistence so the key → thread mapping survives.
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(7, "k", ReviewThreadStatus.Fixed,
            [new ThreadComment("b", "bot", true, "finding", t0)]));

        var regressed = FindingOnLine(2);
        regressed.DedupeKey = "k";
        regressed.IsRegression = true;

        var ctx = Ctx(source);
        ctx.Collector.MarkRegressed("k");
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [regressed], Uncertainties = []};
        ctx.AcceptedFindings = [regressed];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.DoesNotContain(source.PostedFindings, p => p.Finding.DedupeKey == "k");
        Assert.Equal(7, ctx.PostedThreadIds["k"]);
    }

    [Fact]
    public async Task Publish_posts_inline_general_summary_and_vote()
    {
        var source = new FakePullRequestSource();
        var inline = FindingOnLine(2);
        var general = FindingOnLine(2, "vanished");
        general.AnchorDowngraded = true;
        general.DedupeKey = "k-gen";

        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.WorkItems = [new WorkItem(1, "wi", "Bug", null, "AC", "Active")];
        ctx.AcceptedFindings = [inline, general];
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative
            {
                ReviewSummary = "sum",
                AcceptanceCriteria = [new AcVerdict(1, "AC", AcStatus.Unmet, "missing")],
            },
            Findings = [inline, general],
            Uncertainties = [new ReviewUncertainty("t", "q", null)],
        };

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(source.PostedFindings);
        Assert.Equal(1000, ctx.PostedThreadIds["k2"]);
        Assert.Equal(2, source.GeneralComments.Count); // downgraded finding + summary
        Assert.Equal("k-gen", source.GeneralCommentDedupeKeys[0]);
        Assert.Null(source.GeneralCommentDedupeKeys[1]);
        Assert.Contains("❌", source.GeneralComments[1]);
        Assert.Single(source.Votes);
        Assert.Equal(ReviewerVote.WaitingForAuthor, source.Votes[0].Vote);
        Assert.Equal("user-1", source.Votes[0].ReviewerId);
    }

    [Fact]
    public async Task Publish_aborts_when_host_claim_is_lost()
    {
        var ctx = Ctx(new FakePullRequestSource());
        ctx.PublishGuard = () => false;
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ReviewSummary = "sum"},
            Findings = [],
            Uncertainties = [],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(new FakePullRequestSource(), new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Publish_sets_waiting_for_author_vote_via_domain_enum()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [FindingOnLine(2)];
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ReviewSummary = "sum"},
            Findings = ctx.AcceptedFindings,
            Uncertainties = [],
        };

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(source.Votes);
        Assert.Equal(ReviewerVote.WaitingForAuthor, source.Votes[0].Vote);
    }

    [Fact]
    public async Task Publish_resets_vote_on_clean_run()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative {ReviewSummary = "clean"}, Findings = [], Uncertainties = []};
        ctx.AcceptedFindings = [];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        var vote = Assert.Single(source.Votes);
        Assert.Equal(ReviewerVote.NoResponse, vote.Vote);
        Assert.Equal("user-1", vote.ReviewerId);
        Assert.Single(source.GeneralComments); // summary only
    }

    [Fact]
    public async Task Publish_clean_vote_configurable_to_approved()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative {ReviewSummary = "clean"}, Findings = [], Uncertainties = []};
        ctx.AcceptedFindings = [];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance, ReviewerVote.Approved)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ReviewerVote.Approved, Assert.Single(source.Votes).Vote);
    }

    [Fact]
    public async Task Publish_clean_vote_none_leaves_vote_untouched()
    {
        var source = new FakePullRequestSource();
        var ctx = Ctx(source);
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative {ReviewSummary = "clean"}, Findings = [], Uncertainties = []};
        ctx.AcceptedFindings = [];

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance, cleanVote: null)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.Votes);
    }

    [Fact]
    public async Task Publish_still_sets_waiting_for_author_when_findings_exist()
    {
        var source = new FakePullRequestSource();
        var finding = FindingOnLine(2);
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [finding];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance, ReviewerVote.Approved)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ReviewerVote.WaitingForAuthor, Assert.Single(source.Votes).Vote);
    }

    [Fact]
    public async Task Publish_posts_findings_concurrently_within_semaphore()
    {
        var source = new SlowFakePullRequestSource(delayMs: 50);
        var findings = Enumerable.Range(0, 8).Select(i => FindingOnLine(2 + i)).ToArray();
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = findings;
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ReviewSummary = "sum"},
            Findings = findings,
            Uncertainties = [],
        };

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.InRange(source.MaxConcurrentFindingPosts, 2, PublishFindingsStage.MaxConcurrentPosts);
        Assert.Equal(8, source.PostedFindings.Count);
    }

    [Fact]
    public async Task Publish_summary_always_posts_after_findings()
    {
        var source = new SlowFakePullRequestSource();
        var findings = Enumerable.Range(0, 8).Select(i => FindingOnLine(2 + i)).ToArray();
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = findings;
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ReviewSummary = "sum"},
            Findings = findings,
            Uncertainties = [],
        };

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(9, source.WriteLog.Count); // 8 findings, then the summary
        Assert.All(source.WriteLog.Take(8), entry => Assert.StartsWith("finding ", entry));
        Assert.Equal("comment 0", source.WriteLog[8]);
    }

    [Fact]
    public async Task Persist_saves_run_with_thread_ids()
    {
        var store = new FakeFindingStore();
        var finding = FindingOnLine(2);
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [finding];
        ctx.PostedThreadIds = new Dictionary<string, int> {["k2"] = 1000};

        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        var run = Assert.Single(store.Runs);
        Assert.Equal(ctx.RunId, run.Id);
        Assert.Equal("head-sha", run.HeadSha);
        Assert.True(run.Success);
        Assert.Equal(1000, run.Findings[0].ThreadId);
    }

    [Fact]
    public async Task Persist_carries_forward_prior_findings()
    {
        var store = new FakeFindingStore();
        var accepted = FindingOnLine(3);
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [accepted];
        ctx.PostedThreadIds = new Dictionary<string, int> {["k3"] = 1000};
        ctx.PriorRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, ["k2"],
            [new StoredFinding("k2", "r", "high", "t", "src/A.cs", 2, 42)]);

        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        var run = Assert.Single(store.Runs);
        Assert.Equal(2, run.Findings.Count);
        var carried = Assert.Single(run.Findings, f => f.DedupeKey == "k2");
        Assert.Equal(42, carried.ThreadId);
        var acceptedRow = Assert.Single(run.Findings, f => f.DedupeKey == "k3");
        Assert.Equal(1000, acceptedRow.ThreadId);
    }

    [Fact]
    public async Task Persist_does_not_duplicate_key_accepted_again()
    {
        var store = new FakeFindingStore();
        var accepted = FindingOnLine(2);
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [accepted];
        ctx.PostedThreadIds = new Dictionary<string, int> {["k2"] = 1000};
        ctx.PriorRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, ["k2"],
            [new StoredFinding("k2", "r", "high", "t", "src/A.cs", 2, 42)]);

        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        var run = Assert.Single(store.Runs);
        var finding = Assert.Single(run.Findings); // prior "k2" not carried; accepted "k2" wins
        Assert.Equal("k2", finding.DedupeKey);
        Assert.Equal(1000, finding.ThreadId);
    }

    [Fact]
    public async Task BeginRun_persists_inflight_shell_with_all_known_keys()
    {
        var store = new FakeFindingStore();
        var accepted = FindingOnLine(3); // k3
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [accepted];
        ctx.PriorRun = new PriorRun(Key, "sha", DateTimeOffset.UtcNow, ["k2"],
            [new StoredFinding("k2", "r", "high", "t", "src/A.cs", 2, 42)]);

        await new BeginRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        var run = Assert.Single(store.Runs);
        Assert.False(run.Success);
        Assert.Null(run.CompletedAt);
        Assert.Equal(2, run.Findings.Count);
        Assert.Contains(run.Findings, f => f.DedupeKey == "k3" && f.ThreadId == null);
        Assert.Contains(run.Findings, f => f.DedupeKey == "k2" && f.ThreadId == 42);
    }

    [Fact]
    public async Task Publish_backfills_thread_id_per_finding_as_posted()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var f1 = FindingOnLine(2); // k2
        var f2 = FindingOnLine(3); // k3
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [f1, f2];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [f1, f2], Uncertainties = []};

        await new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, store.ThreadIdBackfills.Count);
        Assert.Contains(store.ThreadIdBackfills, b => b.Key == "k2");
        Assert.Contains(store.ThreadIdBackfills, b => b.Key == "k3");
    }

    [Fact]
    public async Task Publish_skips_finding_with_existing_live_thread()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var finding = FindingOnLine(2); // k2
        source.Threads.Add(new ReviewThread(1000, "k2", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", DateTimeOffset.UtcNow)]));
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [finding];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.PostedFindings);
    }

    [Fact]
    public async Task Publish_posts_when_matching_thread_is_fixed()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var finding = FindingOnLine(2); // k2
        source.Threads.Add(new ReviewThread(1000, "k2", ReviewThreadStatus.Fixed,
            [new ThreadComment("b", "bot", true, "finding", DateTimeOffset.UtcNow)]));
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [finding];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(source.PostedFindings);
    }

    [Fact]
    public async Task Publish_partial_failure_keeps_successful_backfills()
    {
        var source = new FakePullRequestSource {ThrowOnPostKey = "k3"};
        var store = new FakeFindingStore();
        var f1 = FindingOnLine(2); // k2 posts
        var f2 = FindingOnLine(3); // k3 throws
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [f1, f2];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [f1, f2], Uncertainties = []};

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains(store.ThreadIdBackfills, b => b.Key == "k2"); // successful post backfilled before the fault
        Assert.DoesNotContain(store.ThreadIdBackfills, b => b.Key == "k3");
    }

    [Fact]
    public async Task Persist_run_persists_max_observed_comment_timestamp()
    {
        var store = new FakeFindingStore();
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t2 = DateTimeOffset.UtcNow;
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.Threads =
        [
            new ReviewThread(1, "k1", ReviewThreadStatus.Active,
                [new ThreadComment("b", "bot", true, "note", t1)]),
            new ReviewThread(2, null, ReviewThreadStatus.Active,
                [new ThreadComment("u", "human", false, "reply", t2)]),
        ];
        ctx.AcceptedFindings = [];
        ctx.PostedThreadIds = new Dictionary<string, int>();

        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(t2, Assert.Single(store.Runs).LastObservedCommentAt);
    }

    [Fact]
    public async Task Persist_run_without_threads_persists_null_watermark()
    {
        var store = new FakeFindingStore();
        var ctx = Ctx();
        ctx.Kind = ReviewKind.Full;
        ctx.Threads = [];
        ctx.AcceptedFindings = [];
        ctx.PostedThreadIds = new Dictionary<string, int>();

        await new PersistRunStage(store).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(Assert.Single(store.Runs).LastObservedCommentAt);
    }

    [Fact]
    public async Task Triage_throws_when_claim_lost_before_writes()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(1, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0), new ThreadComment("u", "human", false, "reply", t0)]));

        var ctx = Ctx(source);
        ctx.PublishGuard = () => false;
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ThreadActions = [new ThreadAction(1, ThreadActionKind.Answer, "answer")]},
            Findings = [],
            Uncertainties = [],
        };
        ctx.AcceptedFindings = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Empty(source.Replies);
        Assert.Empty(source.StatusChanges);
    }

    [Fact]
    public async Task Triage_stops_mid_batch_when_claim_lost()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(1, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0), new ThreadComment("u", "human", false, "reply", t0)]));
        source.Threads.Add(new ReviewThread(2, "k2", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0), new ThreadComment("u", "human", false, "reply", t0)]));

        var ctx = Ctx(source);
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative
            {
                ThreadActions =
                [
                    new ThreadAction(1, ThreadActionKind.Answer, "answer 1"),
                    new ThreadAction(2, ThreadActionKind.Answer, "answer 2"),
                ],
            },
            Findings = [],
            Uncertainties = [],
        };
        ctx.AcceptedFindings = [];

        var guardCalls = 0;
        ctx.PublishGuard = () => Interlocked.Increment(ref guardCalls) <= 2; // before-triage + one op pass

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(source.Replies); // exactly one reply before the claim was lost
    }

    [Fact]
    public async Task Triage_retry_does_not_duplicate_reply()
    {
        var source = new FakePullRequestSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(1, "k1", ReviewThreadStatus.Active,
        [
            new ThreadComment("b", "bot", true, "finding", t0),
            new ThreadComment("u", "human", false, "why?", t0.AddMinutes(1)),
            new ThreadComment("b", "bot", true, CommentFormatter.WithBotPreamble("answer"), t0.AddMinutes(2)),
        ]));

        var ctx = Ctx(source);
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ThreadActions = [new ThreadAction(1, ThreadActionKind.Resolve, "answer")]},
            Findings = [],
            Uncertainties = [],
        };
        ctx.AcceptedFindings = [];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.Replies); // reply already posted by a previous attempt
        Assert.Contains(source.StatusChanges, s => s.ThreadId == 1 && s.Status == ReviewThreadStatus.Fixed);
    }

    [Fact]
    public async Task Triage_skips_reply_when_competitor_posted_after_fetch()
    {
        var source = new CompetitorRepliedSource();
        var t0 = DateTimeOffset.UtcNow;
        source.Threads.Add(new ReviewThread(1, "k1", ReviewThreadStatus.Active,
            [new ThreadComment("b", "bot", true, "finding", t0), new ThreadComment("u", "human", false, "why?", t0)]));

        var ctx = Ctx(source);
        ctx.Result = new ReviewResult
        {
            Narrative = new ReviewNarrative {ThreadActions = [new ThreadAction(1, ThreadActionKind.Resolve, "answer")]},
            Findings = [],
            Uncertainties = [],
        };
        ctx.AcceptedFindings = [];

        await new TriageThreadsStage(source, NullLogger<TriageThreadsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(source.Replies); // competitor's reply detected on re-fetch
        Assert.Contains(source.StatusChanges, s => s.ThreadId == 1 && s.Status == ReviewThreadStatus.Fixed);
    }

    [Fact]
    public async Task Publish_stops_mid_fanout_when_claim_lost()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var findings = Enumerable.Range(0, 3).Select(i => FindingOnLine(2 + i)).ToArray();
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = findings;
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = findings, Uncertainties = []};

        var guardCalls = 0;
        ctx.PublishGuard = () => Interlocked.Increment(ref guardCalls) <= 2; // before-publish + one post pass

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(source.PostedFindings); // exactly one post before the claim was lost
        Assert.Empty(source.GeneralComments); // summary never reached
        Assert.Empty(source.Votes); // vote never reached
    }

    [Fact]
    public async Task Publish_guard_false_before_summary_blocks_summary_and_vote()
    {
        var source = new FakePullRequestSource();
        var store = new FakeFindingStore();
        var finding = FindingOnLine(2);
        var ctx = Ctx(source);
        ctx.Kind = ReviewKind.Full;
        ctx.AcceptedFindings = [finding];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        var guardCalls = 0;
        ctx.PublishGuard = () => Interlocked.Increment(ref guardCalls) <= 2; // before-publish + post pass, summary fails

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None));

        Assert.Single(source.PostedFindings); // finding posted
        Assert.Empty(source.GeneralComments); // summary blocked
        Assert.Empty(source.Votes); // vote blocked
    }

    private static PullRequest Pr(string head)
        => new(1, "t", null, head, "base", "url", false);

    /// <summary>Returns the updated head on every fetch after the first (the stage-1 fetch).</summary>
    private sealed class HeadChangingSource(PullRequest first, PullRequest second) : FakePullRequestSource
    {
        private int _Fetches;

        public override Task<PullRequest> GetPullRequestAsync(PrKey pr, CancellationToken ct)
            => Task.FromResult(_Fetches++ == 0 ? first : second);
    }

    private static ReviewContext HeadCtx(IPullRequestSource source, string reviewedHead)
    {
        var ctx = new ReviewContext(Key, DateTimeOffset.UtcNow)
        {
            PullRequest = Pr(reviewedHead),
            CurrentUser = new CurrentUser("user-1", "reviewforge bot"),
            Kind = ReviewKind.Full,
            AcceptedFindings = [FindingOnLine(2)],
            Result = new ReviewResult
            {
                Narrative = new ReviewNarrative {ReviewSummary = "sum"},
                Findings = [FindingOnLine(2)],
                Uncertainties = [],
            },
        };
        return ctx;
    }

    [Fact]
    public async Task Publish_head_unchanged_publishes_normally()
    {
        var source = new HeadChangingSource(Pr("head-a"), Pr("head-a"));
        await source.GetPullRequestAsync(Key, CancellationToken.None); // the stage-1 fetch
        var ctx = HeadCtx(source, "head-a");

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(source.PostedFindings);
        Assert.Equal(ReviewerVote.WaitingForAuthor, Assert.Single(source.Votes).Vote);
    }

    [Fact]
    public async Task Publish_head_changed_throws_before_any_write()
    {
        var source = new HeadChangingSource(Pr("head-a"), Pr("head-b"));
        await source.GetPullRequestAsync(Key, CancellationToken.None); // the stage-1 fetch
        var ctx = HeadCtx(source, "head-a");

        await Assert.ThrowsAsync<PrHeadChangedException>(() =>
            new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Empty(source.PostedFindings);
        Assert.Empty(source.GeneralComments);
        Assert.Empty(source.Votes);
    }

    [Fact]
    public async Task Publish_head_changed_message_exposes_both_shas()
    {
        var source = new HeadChangingSource(Pr("head-a"), Pr("head-b"));
        await source.GetPullRequestAsync(Key, CancellationToken.None);
        var ctx = HeadCtx(source, "head-a");

        var ex = await Assert.ThrowsAsync<PrHeadChangedException>(() =>
            new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
                .ExecuteAsync(ctx, CancellationToken.None));

        Assert.Equal("head-a", ex.Expected);
        Assert.Equal("head-b", ex.Actual);
        Assert.Contains("head-a", ex.Message);
        Assert.Contains("head-b", ex.Message);
    }

    [Fact]
    public async Task Publish_head_comparison_is_case_insensitive()
    {
        var source = new HeadChangingSource(Pr("head-a"), Pr("HEAD-A"));
        await source.GetPullRequestAsync(Key, CancellationToken.None);
        var ctx = HeadCtx(source, "head-a");

        await new PublishFindingsStage(source, new FakeFindingStore(), NullLogger<PublishFindingsStage>.Instance)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(source.PostedFindings); // same SHA in different casing must not abort
    }

    [Fact]
    public async Task Publish_head_changed_run_is_not_persisted()
    {
        var source = new HeadChangingSource(Pr("head-a"), Pr("head-b"));
        await source.GetPullRequestAsync(Key, CancellationToken.None); // the stage-1 fetch
        var store = new FakeFindingStore();
        var ctx = HeadCtx(source, "head-a");
        var pipeline = new ReviewPipeline(
            [
                new PublishFindingsStage(source, store, NullLogger<PublishFindingsStage>.Instance),
                new PersistRunStage(store),
            ],
            NullLogger<ReviewPipeline>.Instance);

        await Assert.ThrowsAsync<PrHeadChangedException>(() => pipeline.RunAsync(ctx, CancellationToken.None));

        Assert.Empty(store.Runs); // PersistRunStage never ran; the new head is not marked reviewed
        Assert.Empty(source.PostedFindings);
    }

    [Fact]
    public async Task Validate_rejects_symlink_escaping_checkout()
    {
        var outside = Path.Combine(Path.GetTempPath(), "rf-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "MARKER-UNIQUE-SECRET");
        try
        {
            Directory.CreateDirectory(Path.Combine(_RepoDir, "linked"));
            try
            {
                File.CreateSymbolicLink(Path.Combine(_RepoDir, "linked", "secret.txt"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // Windows symlinks require Developer Mode or SeCreateSymbolicLinkPrivilege.
            }

            var finding = FindingOnLine(2) with {Anchor = new FindingAnchor("linked/secret.txt", 1, 1), Snippet = "MARKER-UNIQUE-SECRET"};
            var ctx = Ctx();
            ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

            await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

            Assert.Empty(ctx.AcceptedFindings);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Validate_accepts_symlink_staying_inside_checkout()
    {
        Directory.CreateDirectory(Path.Combine(_RepoDir, "real"));
        File.WriteAllText(Path.Combine(_RepoDir, "real", "b.txt"), "MARKER-INSIDE");
        Directory.CreateDirectory(Path.Combine(_RepoDir, "a"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(_RepoDir, "a", "b.txt"), Path.Combine(_RepoDir, "real", "b.txt"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Windows symlinks require Developer Mode or SeCreateSymbolicLinkPrivilege.
        }

        var finding = FindingOnLine(2) with {Anchor = new FindingAnchor("a/b.txt", 1, 1), Snippet = "MARKER-INSIDE"};
        var ctx = Ctx();
        ctx.Diff = DiffIndex.Parse("+++ b/a/b.txt\n@@ -0,0 +1,1 @@\n+MARKER-INSIDE\n");
        ctx.ChangedFileManifest = [new ChangedFile("a/b.txt", ChangedFileType.Edit)];
        ctx.Result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [finding], Uncertainties = []};

        await new ValidateFindingsStage(NullLogger<ValidateFindingsStage>.Instance).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.AcceptedFindings);
    }

    private sealed class CompetitorRepliedSource : FakePullRequestSource
    {
        public override Task<IReadOnlyList<ReviewThread>> GetThreadsAsync(PrKey pr, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ReviewThread>>(Threads
                .Select(t => new ReviewThread(t.Id, t.DedupeKey, t.Status,
                    [.. t.Comments, new ThreadComment("b", "bot", true, CommentFormatter.WithBotPreamble("answer"), DateTimeOffset.UtcNow)]))
                .ToArray());
    }
}

public class CommentFormatterTests
{
    [Fact]
    public void Finding_uses_modern_hierarchy_and_downgrade_notice()
    {
        var finding = new RichFinding
        {
            RuleId = "r", Title = "t", Severity = "high", Category = "bug", Description = "d",
            Suggestion = "fix it", Anchor = new FindingAnchor("f.cs", 3, 3), AnchorDowngraded = true,
        };
        var text = CommentFormatter.FormatFinding(finding);
        Assert.Contains("### 🔴 t", text);
        Assert.Contains("**Rule:** `r`", text);
        Assert.Contains("**Category:** `bug`", text);
        Assert.Contains("**Suggested fix**", text);
        Assert.Contains("fix it", text);
        Assert.Contains("Location could not be verified", text);
        Assert.Contains("f.cs:3", text);
    }

    [Fact]
    public void FormatFinding_marks_regressions_with_prefix()
    {
        var finding = new RichFinding
        {
            RuleId = "r", Title = "t", Severity = "high", Category = "bug", Description = "d",
            IsRegression = true,
        };

        var text = CommentFormatter.FormatFinding(finding);

        Assert.Contains("### 🔴 ⚠️ regressed: t", text);
    }

    [Fact]
    public void Finding_without_suggestion_omits_suggestion_section()
    {
        var finding = new RichFinding
        {
            RuleId = "r", Title = "t", Severity = "info", Category = "docs", Description = "d",
        };

        var text = CommentFormatter.FormatFinding(finding);

        Assert.DoesNotContain("Suggested fix", text);
        Assert.DoesNotContain("Location could not be verified", text);
    }

    [Fact]
    public void Summary_renders_all_sections()
    {
        var result = new ReviewResult
        {
            Narrative = new ReviewNarrative
            {
                PrSummary = "what",
                ReviewSummary = "assessment",
                VerificationSummary = "verified",
                GoodPractices = ["nice tests"],
                AcceptanceCriteria =
                [
                    new AcVerdict(1, "met-one", AcStatus.Met, null),
                    new AcVerdict(1, "unmet-one", AcStatus.Unmet, "evidence"),
                    new AcVerdict(2, "unclear-one", AcStatus.Unclear, null),
                ],
            },
            Findings =
            [
                new RichFinding {RuleId = "r", Title = "finding", Severity = "medium", Category = "bug", Description = "d"},
            ],
            Uncertainties = [new ReviewUncertainty("topic", "question", null)],
        };

        var text = CommentFormatter.FormatSummary(result,
            [new WorkItem(1, "wi", "Bug", null, "ac", "Active")], [42], ReviewKind.FollowUp);
        Assert.Contains("### Verification", text);
        Assert.Contains("verified", text);
        Assert.Contains("### Findings", text);
        Assert.Contains("🟠 **1 medium**", text);

        Assert.Contains("follow-up review", text);
        Assert.Contains("✅", text);
        Assert.Contains("❌", text);
        Assert.Contains("❓", text);
        Assert.Contains("nice tests", text);
        Assert.Contains("question", text);
        Assert.Contains("#42", text);
    }

    [Fact]
    public void Summary_minimal_when_nothing_to_report()
    {
        var result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []};
        var text = CommentFormatter.FormatSummary(result, [], [], ReviewKind.Full);
        Assert.Contains("full review", text);
        Assert.Contains("**Findings:** **0**", text);
        Assert.DoesNotContain("Acceptance criteria", text);
    }

    [Fact]
    public void FormatFinding_starts_with_bot_preamble()
    {
        var finding = new RichFinding {RuleId = "r", Title = "t", Severity = "info", Category = "docs", Description = "d"};
        Assert.StartsWith(CommentFormatter.BotPreamble, CommentFormatter.FormatFinding(finding));
    }

    [Fact]
    public void FormatSummary_starts_with_bot_preamble()
    {
        var result = new ReviewResult {Narrative = new ReviewNarrative(), Findings = [], Uncertainties = []};
        Assert.StartsWith(CommentFormatter.BotPreamble, CommentFormatter.FormatSummary(result, [], [], ReviewKind.Full));
    }

    [Fact]
    public void WithBotPreamble_prepends_once_and_trims()
    {
        var text = CommentFormatter.WithBotPreamble("  hello  ");
        Assert.StartsWith(CommentFormatter.BotPreamble, text);
        Assert.EndsWith("hello", text);
        Assert.Equal(1, text.Split(CommentFormatter.BotPreamble).Length - 1);
    }
}