using ReviewForge.Core.Analysis;
using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class ScopeTests
{
    [Fact]
    public void RecordFinding_rejects_file_outside_changed_files()
    {
        var collector = new ReviewCollector();
        var tools = new ReviewTools(
            collector,
            new ContextStore(),
            changedFiles: new HashSet<string>(["src/Changed.cs"], StringComparer.OrdinalIgnoreCase),
            diff: DiffIndex.Parse("+++ b/src/Changed.cs\n@@ -0,0 +1,1 @@\n+changed\n"));

        var result = tools.RecordFinding(
            "general.other", "pre-existing", "high", "bug", "d", "unchanged", "fix",
            "src/Unchanged.cs", 1, 1);

        Assert.Contains("outside the current pull-request diff", result);
        Assert.Empty(collector.Findings);
    }

    [Fact]
    public void RecordFinding_rejects_unchanged_line_in_changed_file()
    {
        var collector = new ReviewCollector();
        var tools = new ReviewTools(
            collector,
            new ContextStore(),
            changedFiles: new HashSet<string>(["src/Changed.cs"], StringComparer.OrdinalIgnoreCase),
            diff: DiffIndex.Parse("+++ b/src/Changed.cs\n@@ -0,0 +2,1 @@\n+changed\n"));

        var result = tools.RecordFinding(
            "general.other", "old line", "high", "bug", "d", "unchanged", "fix",
            "src/Changed.cs", 1, 1);

        Assert.Contains("outside the current pull-request diff", result);
        Assert.Empty(collector.Findings);
    }
}