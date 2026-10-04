using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class TriageToolsTests
{
    [Fact]
    public void RecordVerdict_rejects_nonpositive_thread_id()
    {
        var collector = new ReviewCollector();

        Assert.Contains("threadId must be positive", new TriageTools(collector)
            .RecordVerdict(0, "Actionable", "src/a.cs:1", "high"));
        Assert.Empty(collector.ThreadVerdicts);
    }

    [Fact]
    public void RecordVerdict_rejects_threads_outside_the_current_batch()
    {
        var collector = new ReviewCollector();
        var tools = new TriageTools(collector, [1, 2]);

        Assert.Contains("not part of this triage batch", tools.RecordVerdict(3, "Actionable", "src/a.cs:1", "high"));
        Assert.Empty(collector.ThreadVerdicts);
        Assert.StartsWith("recorded verdict", tools.RecordVerdict(2, "Actionable", "src/a.cs:1", "high"));
        Assert.Single(collector.ThreadVerdicts);
    }

    [Fact]
    public void RecordVerdict_rejects_undefined_numeric_verdicts()
    {
        // Enum.TryParse accepts numeric strings for undeclared values; the verdict must
        // round-trip through the domain enum, never an undefined cast.
        var collector = new ReviewCollector();

        Assert.Contains("verdict must be", new TriageTools(collector)
            .RecordVerdict(1, "99", "src/a.cs:1", "high"));
        Assert.Empty(collector.ThreadVerdicts);
    }

    [Theory]
    [InlineData("", "verdict is required")]
    [InlineData("unknown", "verdict must be")]
    [InlineData("Actionable", "evidence is required", "")]
    [InlineData("Actionable", "confidence is required", "src/a.cs:1", "")]
    [InlineData("Actionable", "confidence must be", "src/a.cs:1", "certain")]
    [InlineData("Actionable", "category is required", "src/a.cs:1", "high", "")]
    public void RecordVerdict_rejects_invalid_required_fields(
        string verdict, string expected, string evidence = "src/a.cs:1", string confidence = "high", string category = "bug")
    {
        var result = new TriageTools(new ReviewCollector())
            .RecordVerdict(1, verdict, evidence, confidence, category);

        Assert.Contains(expected, result);
    }

    [Fact]
    public void RecordVerdict_rejects_oversized_answer_and_normalizes_valid_values()
    {
        var collector = new ReviewCollector();
        var tools = new TriageTools(collector);

        Assert.Contains("answer is too long", tools.RecordVerdict(
            1, "Actionable", " evidence ", "HIGH", answer: new string('x', 4001)));
        Assert.StartsWith("recorded verdict", tools.RecordVerdict(
            1, "actionable", " evidence ", "HIGH", " bug ", " answer "));
        var verdict = Assert.Single(collector.ThreadVerdicts);
        Assert.Equal(TriageVerdict.Actionable, verdict.Verdict);
        Assert.Equal("evidence", verdict.Evidence);
        Assert.Equal("high", verdict.Confidence);
        Assert.Equal("bug", verdict.Category);
        Assert.Equal("answer", verdict.Answer);
    }

    [Fact]
    public void TaskDone_requires_summary_then_completes_collector()
    {
        var collector = new ReviewCollector();
        var tools = new TriageTools(collector);

        Assert.Contains("summary is required", tools.TaskDone(" "));
        Assert.False(collector.Done);
        Assert.Equal("triage marked as done", tools.TaskDone(" reviewed "));
        Assert.True(collector.Done);
        Assert.Equal("reviewed", collector.Narrative!.ReviewSummary);
    }
}
