using ReviewForge.Core.Resolve;
using ReviewForge.Core.Resolve.Domain;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ResolvePromptBuilderTests
{
    [Fact]
    public void Triage_prompt_bounds_oversized_pr_supplied_content()
    {
        var comment = new ResolvableComment(
            1,
            new ThreadAnchor("src/a.cs", 1, 1),
            "requester",
            "Requester",
            [new ThreadComment("u", "User", false, new string('h', 10_000), DateTimeOffset.UtcNow)],
            new string('r', 10_000),
            true);
        var workItems = new[]
        {
            new WorkItem(7, new string('t', 5_000), "Task", null, new string('a', 10_000), "Active"),
        };
        var prompt = ResolvePromptBuilder.BuildTriagePrompt([comment], workItems);

        Assert.True(prompt.Length <= 64_000 + 128, $"prompt was {prompt.Length} chars");
        Assert.Contains("truncated", prompt);
        // The thread identity survives truncation — triage needs it for the verdict.
        Assert.Contains("thread=1", prompt);
    }

    [Fact]
    public void Triage_and_fix_prompts_include_all_linked_work_item_context()
    {
        var item = new WorkItem(7, "Bug", "Bug", "description", "criteria", "Active",
            ReproSteps: "reproduce it", SystemInfo: "runtime details",
            Comments:
            [
                new WorkItemComment("Ada", "initial report", DateTimeOffset.Parse("2026-02-01T00:00:00Z")),
                new WorkItemComment("Grace", "additional detail", DateTimeOffset.Parse("2026-02-02T00:00:00Z")),
            ]);
        var triagePrompt = ResolvePromptBuilder.BuildTriagePrompt([], [item]);
        var fixPrompt = ResolvePromptBuilder.BuildFixPrompt([], [item]);

        foreach (var prompt in new[] {triagePrompt, fixPrompt})
        {
            Assert.Contains("Repro steps:", prompt);
            Assert.Contains("reproduce it", prompt);
            Assert.Contains("System info:", prompt);
            Assert.Contains("runtime details", prompt);
            Assert.Contains("Ada (created 2026-02-01T00:00:00.0000000+00:00): initial report", prompt);
            Assert.Contains("Grace (created 2026-02-02T00:00:00.0000000+00:00): additional detail", prompt);
        }
    }


    [Fact]
    public void Triage_prompt_fails_instead_of_truncating_linked_work_item_comments()
    {
        var item = new WorkItem(7, "Bug", "Bug", null, null, "Active",
            Comments: [new WorkItemComment("Ada", new string('x', 70_000))]);

        Assert.Throws<InvalidOperationException>(
            () => ResolvePromptBuilder.BuildTriagePrompt([], [item]));
    }
    [Fact]
    public void Fix_prompt_bounds_request_and_evidence_text()
    {
        var fix = new PlannedFix(
            1,
            new ThreadAnchor("src/a.cs", 1, 1),
            new string('r', 10_000),
            new string('e', 10_000),
            ["src/a.cs"]);

        var prompt = ResolvePromptBuilder.BuildFixPrompt([fix], []);

        Assert.Contains("truncated", prompt);
        Assert.Contains("thread=1", prompt);
    }
}
