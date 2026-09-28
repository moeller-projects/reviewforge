using ReviewForge.Core.AutoFix;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class FindingFixerRegistryTests
{
    [Fact]
    public void Registry_returns_false_for_unknown_rule()
    {
        var fixer = new StubFixer("known");
        var registry = new FindingFixerRegistry([fixer]);

        Assert.False(registry.TryGet("missing", out var resolved));
        Assert.Null(resolved);
        Assert.Equal(["known"], registry.RegisteredRuleIds);
    }

    [Fact]
    public void FixProposal_deconstructs_all_fields()
    {
        var proposal = new FixProposal(
            "src/file.cs", 3, 5, "replacement", "rationale",
            FixOrigin.LlmCommanded, SourceThreadId: 42);

        var (filePath, startLine, endLine, replacement, rationale, origin, sourceThreadId) = proposal;

        Assert.Equal("src/file.cs", filePath);
        Assert.Equal(3, startLine);
        Assert.Equal(5, endLine);
        Assert.Equal("replacement", replacement);
        Assert.Equal("rationale", rationale);
        Assert.Equal(FixOrigin.LlmCommanded, origin);
        Assert.Equal(42, sourceThreadId);
    }

    private sealed class StubFixer(string ruleId) : IFindingFixer
    {
        public string RuleId { get; } = ruleId;

        public FixProposal? TryPropose(FixContext context) => null;
    }
}
