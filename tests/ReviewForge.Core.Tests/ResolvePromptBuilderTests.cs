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
