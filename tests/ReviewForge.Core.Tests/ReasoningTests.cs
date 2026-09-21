using System.Text.Json;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public class ContextStoreTests
{
    [Fact]
    public void Put_and_read_roundtrip()
    {
        var store = new ContextStore();
        store.Put("crg", "payload");
        Assert.Equal("payload", store.Read("crg"));
        Assert.Equal(["crg"], store.Names);
    }

    [Fact]
    public void Unknown_entry_reads_null()
        => Assert.Null(new ContextStore().Read("nope"));

    [Fact]
    public void Read_is_capped()
    {
        var store = new ContextStore();
        store.Put("big", new string('x', ContextStore.MaxReadChars + 10));
        var read = store.Read("big");
        Assert.EndsWith("[truncated]", read);
        Assert.True(read!.Length < ContextStore.MaxReadChars + 20);
    }

    [Fact]
    public void Empty_name_rejected()
        => Assert.Throws<ArgumentException>(() => new ContextStore().Put(" ", "x"));
}

public class ReviewCollectorTests
{
    private static RichFinding Finding(string key) => new()
    {
        RuleId = "r", Title = "t", Severity = "low", Category = "style", Description = "d", DedupeKey = key,
    };

    [Fact]
    public void AddFinding_tracks_keys_and_streams_jsonl()
    {
        var jsonl = new StringWriter();
        var collector = new ReviewCollector(jsonlSink: jsonl);
        collector.AddFinding(Finding("k1"));

        Assert.True(collector.IsKnown("k1"));
        Assert.Single(collector.Findings);
        Assert.Contains("\"k1\"", jsonl.ToString());
    }

    [Fact]
    public void AddFinding_writes_envelope_with_run_metadata()
    {
        var jsonl = new StringWriter();
        var runId = Guid.NewGuid();
        var collector = new ReviewCollector(jsonlSink: jsonl, runId: runId, headSha: "abc123");
        collector.AddFinding(Finding("k1"));

        using var doc = JsonDocument.Parse(jsonl.ToString().Trim());
        Assert.Equal(runId, doc.RootElement.GetProperty("RunId").GetGuid());
        Assert.Equal("abc123", doc.RootElement.GetProperty("HeadSha").GetString());
        Assert.True(doc.RootElement.GetProperty("RecordedAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
        Assert.Equal("k1", doc.RootElement.GetProperty("Finding").GetProperty("DedupeKey").GetString());
    }

    [Fact]
    public void Preseeded_keys_are_known()
    {
        var collector = new ReviewCollector(knownDedupeKeys: ["old"]);
        Assert.True(collector.IsKnown("old"));
        Assert.False(collector.IsKnown("new"));
    }

    [Fact]
    public void Complete_is_idempotent_first_narrative_wins()
    {
        var collector = new ReviewCollector();
        collector.Complete(new ReviewNarrative {ReviewSummary = "first"});
        collector.Complete(new ReviewNarrative {ReviewSummary = "second"});

        Assert.True(collector.Done);
        Assert.Equal("first", collector.Narrative!.ReviewSummary);
    }

    [Fact]
    public void ToResult_defaults_narrative_when_task_done_missing()
    {
        var collector = new ReviewCollector();
        collector.AddUncertainty(new ReviewUncertainty("t", "q", null));
        var result = collector.ToResult("cap reached");

        Assert.Equal("cap reached", result.ReviewDepth);
        Assert.NotNull(result.Narrative);
        Assert.Single(result.Uncertainties);
        Assert.Empty(result.Findings);
    }
}

public class ReviewToolsTests
{
    private static (ReviewTools Tools, ReviewCollector Collector) Create()
    {
        var collector = new ReviewCollector();
        return (new ReviewTools(collector, new ContextStore()), collector);
    }

    [Fact]
    public void RecordFinding_accepts_valid_and_computes_key()
    {
        var (tools, collector) = Create();
        var result = tools.RecordFinding("null-deref", "x may be null", "high", "bug", "deref without check",
            snippet: "x.Value", filePath: "src/F.cs", startLine: 10);

        Assert.StartsWith("recorded finding", result);
        var finding = Assert.Single(collector.Findings);
        Assert.NotNull(finding.DedupeKey);
        Assert.Equal(10, finding.Anchor!.EndLine);
    }

    [Fact]
    public void RecordFinding_rejects_invalid_severity_with_message()
    {
        var (tools, collector) = Create();
        var result = tools.RecordFinding("r", "t", "BLOCKER", "bug", "d");
        Assert.Contains("finding rejected", result);
        Assert.Empty(collector.Findings);
    }

    [Fact]
    public void RecordFinding_rejects_bad_anchor_line()
    {
        var (tools, _) = Create();
        var result = tools.RecordFinding("r", "t", "low", "style", "d", filePath: "f.cs", startLine: 0);
        Assert.Contains("finding rejected", result);
    }

    [Fact]
    public void RecordFinding_dedupes_known_keys()
    {
        var (tools, collector) = Create();
        tools.RecordFinding("r", "t", "low", "style", "d", snippet: "s", filePath: "f.cs", startLine: 1);
        var second = tools.RecordFinding("r", "t", "low", "style", "d", snippet: "s", filePath: "f.cs", startLine: 99);

        Assert.Contains("already recorded", second);
        Assert.Single(collector.Findings);
    }

    [Fact]
    public void RecordFinding_marks_redetected_when_key_is_known()
    {
        var (tools, collector) = Create();
        tools.RecordFinding("r", "t", "low", "style", "d", snippet: "s", filePath: "f.cs", startLine: 1);
        var key = Assert.Single(collector.Findings).DedupeKey;

        var second = tools.RecordFinding("r", "t", "low", "style", "d", snippet: "s", filePath: "f.cs", startLine: 99);

        Assert.Contains("already recorded", second);
        Assert.Contains(key!, collector.RedetectedKeys);
    }

    [Fact]
    public void RecordFinding_without_file_is_pr_level()
    {
        var (tools, collector) = Create();
        tools.RecordFinding("r", "t", "info", "docs", "d");
        Assert.Null(collector.Findings[0].Anchor);
    }

    [Fact]
    public void RecordUncertainty_validates_and_stores()
    {
        var (tools, collector) = Create();
        Assert.Contains("rejected", tools.RecordUncertainty("", "q"));
        Assert.Equal("recorded uncertainty", tools.RecordUncertainty("topic", "question", "ctx"));
        Assert.Single(collector.Uncertainties);
    }

    [Fact]
    public void TaskDone_requires_summary()
    {
        var (tools, collector) = Create();
        Assert.Contains("rejected", tools.TaskDone(""));
        Assert.False(collector.Done);
    }

    [Fact]
    public void TaskDone_stores_narrative_with_ac_and_thread_actions()
    {
        var (tools, collector) = Create();
        var result = tools.TaskDone("summary",
            verificationSummary: "how",
            prSummary: "what",
            goodPractices: ["tests"],
            acceptanceCriteria: [new AcVerdict(1, "criterion", AcStatus.Met, "evidence")],
            threadActions: [new ThreadAction(7, ThreadActionKind.Answer, "reply")]);

        Assert.Equal("review marked as done", result);
        Assert.True(collector.Done);
        Assert.Single(collector.Narrative!.AcceptanceCriteria!);
        Assert.Single(collector.Narrative.ThreadActions!);
    }

    [Fact]
    public void TaskDone_rejects_invalid_nested_items()
    {
        var (tools, _) = Create();
        var result = tools.TaskDone("summary", threadActions: [new ThreadAction(7, ThreadActionKind.Answer, "")]);
        Assert.Contains("rejected", result);
    }

    [Fact]
    public void ReadContext_lists_available_on_miss()
    {
        var store = new ContextStore();
        store.Put("crg", "data");
        var (tools, _) = (new ReviewTools(new ReviewCollector(), store), (ReviewCollector?) null);
        Assert.Equal("data", tools.ReadContext("crg"));
        Assert.Contains("crg", tools.ReadContext("missing"));
    }
}

public class PromptBuilderTests
{
    private static PromptInput BaseInput() => new(
        Pr: new PullRequest(7, "Add feature", "does things", "head", "base", "url", false),
        Kind: ReviewKind.Full,
        WorkItems: [],
        ChangedFiles: ["src/A.cs"],
        PendingReplies: [],
        DiffText: "+++ b/src/A.cs",
        Enrichment: null,
        ContextNames: []);

    [Fact]
    public void Full_review_prompt_contains_pr_and_diff()
    {
        var prompt = PromptBuilder.Build(BaseInput());
        Assert.Contains("full code review", prompt);
        Assert.Contains("Add feature", prompt);
        Assert.Contains("does things", prompt);
        Assert.Contains("src/A.cs", prompt);
        Assert.Contains("```diff", prompt);
    }

    [Fact]
    public void Followup_prompt_emphasizes_delta()
    {
        var prompt = PromptBuilder.Build(BaseInput() with {Kind = ReviewKind.FollowUp});
        Assert.Contains("follow-up review", prompt);
    }

    [Fact]
    public void Work_items_with_ac_render_verdict_instruction()
    {
        var input = BaseInput() with
        {
            WorkItems = [new WorkItem(42, "Story", "User Story", "desc", "AC1: works", "Active")],
        };
        var prompt = PromptBuilder.Build(input);
        Assert.Contains("#42", prompt);
        Assert.Contains("AC1: works", prompt);
        Assert.Contains("verdict", prompt);
    }

    [Fact]
    public void Pending_replies_render_thread_action_instruction()
    {
        var input = BaseInput() with
        {
            PendingReplies = [new PendingReply(5, "key", "anna", "why this?")],
        };
        var prompt = PromptBuilder.Build(input);
        Assert.Contains("Thread 5", prompt);
        Assert.Contains("anna", prompt);
        Assert.Contains("reopen", prompt);
    }

    [Fact]
    public void Context_names_and_enrichment_render()
    {
        var input = BaseInput() with {ContextNames = ["crg"], Enrichment = "graph-data"};
        var prompt = PromptBuilder.Build(input);
        Assert.Contains("read_context", prompt);
        Assert.Contains("graph-data", prompt);
    }

    [Fact]
    public void Small_diff_passes_through_unchanged()
    {
        var input = BaseInput() with {DiffText = "+++ b/a.cs\n@@ -1,1 +1,1 @@\n+x\n"};
        var prompt = PromptBuilder.Build(input);
        Assert.DoesNotContain(PromptBuilder.DiffTruncationMarker, prompt);
    }

    [Fact]
    public void Oversized_diff_is_truncated_with_marker()
    {
        var big = string.Join('\n', Enumerable.Range(0, 10_000).Select(i => $"+line {i}"));
        var input = BaseInput() with {DiffText = big, MaxDiffChars = 5_000, MaxDiffCharsPerFile = 5_000};
        var prompt = PromptBuilder.Build(input);
        Assert.Contains(PromptBuilder.DiffTruncationMarker, prompt);
        Assert.True(prompt.Length < big.Length);
    }

    [Fact]
    public void Truncation_stays_within_the_total_budget()
    {
        var big = string.Join('\n', Enumerable.Range(0, 10_000).Select(i => $"+line {i}"));

        var diff = PromptBuilder.ShrinkDiff(big, maxTotal: 1_000, maxPerFile: 1_000);

        // The reserved marker and fixed '\n' accounting must never push the emitted diff
        // past the advertised total budget.
        Assert.True(diff.Length <= 1_000, $"diff length {diff.Length} exceeded the 1000-char budget");
        Assert.Contains(PromptBuilder.DiffTruncationMarker, diff);
    }

    [Fact]
    public void Per_file_cap_produces_per_file_marker()
    {
        var file = "+++ b/a.cs\n" + string.Join('\n', Enumerable.Range(0, 500).Select(i => $"+x{i}"));
        var input = BaseInput() with {DiffText = file, MaxDiffChars = 1_000_000, MaxDiffCharsPerFile = 200};
        var prompt = PromptBuilder.Build(input);
        Assert.Contains("+++ b/a.cs", prompt);
        Assert.Contains(PromptBuilder.DiffTruncationMarker, prompt);
        Assert.DoesNotContain("x400", prompt);
    }
}

public class SystemPromptComposerTests
{
    [Fact]
    public void Embedded_prompt_loads_and_mentions_contract()
    {
        var prompt = SystemPromptComposer.Compose();
        Assert.Contains("task_done", prompt);
        Assert.Contains("acceptance criterion", prompt);
    }

    [Fact]
    public void Override_file_wins()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".md");
        File.WriteAllText(path, "custom prompt");
        try
        {
            Assert.Equal("custom prompt", SystemPromptComposer.Compose(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}