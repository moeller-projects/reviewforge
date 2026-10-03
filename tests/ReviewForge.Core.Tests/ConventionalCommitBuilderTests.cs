using ReviewForge.Core.AutoFix;
using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ConventionalCommitBuilderTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static RichFinding Finding(
        string title, string severity = "medium", string category = "bug")
        => new()
        {
            RuleId = "r", Title = title, Severity = severity, Category = category, Description = "d",
        };

    private static AppliedFix Fix(
        string path, int start = 1, int end = 2, string rationale = "why", int? threadId = null)
        => new(
            threadId is null ? "key" : $"thread-{threadId}",
            new FixProposal(path, start, end, "x", rationale, threadId is null ? FixOrigin.Deterministic : FixOrigin.LlmCommanded, threadId));

    private static ConventionalCommitBuilder.CommitFixInput Input(AppliedFix fix, RichFinding? finding = null)
        => new(fix, finding);

    [Fact]
    public void Build_throws_for_an_empty_fix_set()
        => Assert.Throws<ArgumentException>(() => ConventionalCommitBuilder.Build(RunId, []));

    [Theory]
    [InlineData("bug", "fix")]
    [InlineData("security", "fix")]
    [InlineData("performance", "perf")]
    [InlineData("style", "style")]
    [InlineData("docs", "docs")]
    public void Build_maps_category_to_conventional_type(string category, string expectedType)
    {
        var message = ConventionalCommitBuilder.Build(
            RunId, [Input(Fix("src/a.sh"), Finding("title", category: category))]);

        Assert.StartsWith($"{expectedType}(src): ", message);
    }

    [Theory]
    // The highest-severity finding decides the type of a mixed set: a security fix must
    // never ship under a "style" type.
    [InlineData(new[] {"style", "security"}, "fix")]
    [InlineData(new[] {"docs", "performance"}, "perf")]
    [InlineData(new[] {"style", "docs"}, "style")] // same severity: first finding wins
    public void Build_picks_the_highest_severity_category_for_mixed_sets(string[] categories, string expectedType)
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh"), Finding("one", category: categories[0])),
            Input(Fix("src/b.sh"), Finding("two", category: categories[1])),
        ]);

        Assert.StartsWith($"{expectedType}(src): ", message);
    }

    [Theory]
    [InlineData("critical", "fix")]
    [InlineData("info", "perf")] // medium performance outranks info bug
    public void Build_orders_severity_before_category(string bugSeverity, string expectedType)
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh"), Finding("bug finding", severity: bugSeverity, category: "bug")),
            Input(Fix("src/b.sh"), Finding("perf finding", severity: "medium", category: "performance")),
        ]);

        Assert.StartsWith($"{expectedType}(src): ", message);
    }

    [Fact]
    public void Build_uses_fix_type_for_commanded_fixes_without_findings()
    {
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix("src/a.sh", threadId: 7))]);

        Assert.StartsWith("fix(src): address review thread #7", message);
    }

    [Theory]
    [InlineData("src/util/helper.sh", "src")]       // top-level directory
    [InlineData("deploy.sh", "deploy")]             // repo-root file: extension-less name
    [InlineData("a.b/c.sh", "a.b")]                 // dots survive
    [InlineData("My Src/x.sh", "my-src")]           // lowercased, spaces sanitized
    [InlineData("café/x.sh", "caf")]                // non-ASCII letters stripped to dashes and trimmed
    public void Build_sanitizes_the_scope(string path, string expectedScope)
    {
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix(path), Finding("title"))]);

        Assert.StartsWith($"fix({expectedScope}): ", message);
    }

    [Fact]
    public void Build_omits_the_scope_across_top_level_directories()
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh"), Finding("one")),
            Input(Fix("docs/b.md"), Finding("two")),
        ]);

        Assert.StartsWith("fix: address 2 review findings across 2 files", message);
    }

    [Fact]
    public void Build_coalesces_same_file_fixes_into_one_scoped_subject()
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh"), Finding("one")),
            Input(Fix("src/a.sh", start: 5, end: 6), Finding("two")),
        ]);

        Assert.StartsWith("fix(src): address 2 review findings in src/a.sh", message);
    }

    [Fact]
    public void Build_truncates_the_subject_at_72_characters()
    {
        var message = ConventionalCommitBuilder.Build(
            RunId, [Input(Fix("src/a.sh"), Finding(new string('t', 200)))]);

        var subject = ConventionalCommitBuilder.SubjectOf(message);
        Assert.Equal(ConventionalCommitBuilder.MaxSubjectLength, subject.Length);
        Assert.Equal(subject, message.Split('\n')[0]);
    }

    [Fact]
    public void Build_wraps_body_bullets_at_72_characters()
    {
        var rationale = string.Join(' ', Enumerable.Repeat("word", 40));
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix("src/a.sh", rationale: rationale))]);

        foreach (var line in message.Split('\n'))
        {
            Assert.True(line.Length <= ConventionalCommitBuilder.BodyWrapWidth, $"line too long: {line}");
        }

        // A wrapped bullet continues indented under the bullet text.
        Assert.Contains("\n  word", message);
    }

    [Fact]
    public void Build_hard_breaks_tokens_longer_than_the_wrap_width()
    {
        var message = ConventionalCommitBuilder.Build(
            RunId, [Input(Fix("src/a.sh", rationale: new string('x', 100)))]);

        foreach (var line in message.Split('\n'))
        {
            Assert.True(line.Length <= ConventionalCommitBuilder.BodyWrapWidth, $"line too long: {line}");
        }
    }

    [Fact]
    public void Build_lists_every_fix_as_a_body_bullet()
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh", start: 1, end: 2, rationale: "first")),
            Input(Fix("src/a.sh", start: 8, end: 9, rationale: "second")),
        ]);

        Assert.Contains("- `src/a.sh:1–2` — first", message);
        Assert.Contains("- `src/a.sh:8–9` — second", message);
    }

    [Fact]
    public void Build_emits_the_run_trailer_in_the_trailer_block()
    {
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix("src/a.sh"), Finding("title"))]);

        Assert.EndsWith($"ReviewForge-Run: {RunId:D}\n", message);
        Assert.True(LoopGuard.TryParseRunTrailer(message, out var parsed));
        Assert.Equal(RunId, parsed);
    }

    [Fact]
    public void Build_emits_thread_trailers_for_commanded_fixes()
    {
        var message = ConventionalCommitBuilder.Build(RunId,
        [
            Input(Fix("src/a.sh", threadId: 9)),
            Input(Fix("src/b.sh", threadId: 3)),
            Input(Fix("src/c.sh", threadId: 9)), // duplicate: emitted once
        ]);

        var trailerBlock = message.Split("\n\n")[^1];
        Assert.Contains("ReviewForge-Thread: 3\n", trailerBlock);
        Assert.Contains("ReviewForge-Thread: 9\n", trailerBlock);
        Assert.Equal(1, trailerBlock.Split("ReviewForge-Thread: 9").Length - 1);
        // Trailer order: run trailer first, thread trailers ascending.
        Assert.True(trailerBlock.IndexOf("ReviewForge-Run:", StringComparison.Ordinal)
                    < trailerBlock.IndexOf("ReviewForge-Thread: 3", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_omits_thread_trailers_for_deterministic_fixes()
    {
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix("src/a.sh"), Finding("title"))]);

        Assert.DoesNotContain("ReviewForge-Thread:", message);
    }

    [Fact]
    public void Build_separates_subject_body_and_trailers_with_blank_lines()
    {
        var message = ConventionalCommitBuilder.Build(RunId, [Input(Fix("src/a.sh"), Finding("title"))]);
        var parts = message.Split("\n\n");

        Assert.Equal(3, parts.Length);
        Assert.StartsWith("fix(src):", parts[0]);
        Assert.StartsWith("- `src/a.sh", parts[1]);
        Assert.StartsWith("ReviewForge-Run:", parts[2]);
    }
}
