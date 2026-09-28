using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests.AnalysisTests;

public sealed class ShardPlannerTests
{
    private static string Block(string file, int bodyLines = 5) =>
        $"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n" +
        string.Concat(Enumerable.Repeat($"line in {file}\n", bodyLines));

    [Fact]
    public void Plan_SmallDiff_SingleShard_LegacyPath()
    {
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");
        var plan = ShardPlanner.Plan(a + b, a.Length + b.Length, 4);

        Assert.False(plan.Overflowed);
        Assert.Single(plan.Shards);
        Assert.Equal(["src/a.cs", "src/b.cs"], plan.Shards[0].Files);
        Assert.Equal(a + b, plan.Shards[0].DiffText);
    }

    [Fact]
    public void Plan_FitsTwoShards_BucketsByCumulativeSize()
    {
        // Each block exactly fills the budget on its own; together they must split.
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");
        var plan = ShardPlanner.Plan(a + b, a.Length, 4);

        Assert.Equal(2, plan.Shards.Count);
        Assert.Equal(["src/a.cs"], plan.Shards[0].Files);
        Assert.Equal(a, plan.Shards[0].DiffText);
        Assert.Equal(["src/b.cs"], plan.Shards[1].Files);
        Assert.Equal(b, plan.Shards[1].DiffText);
    }

    [Fact]
    public void Plan_SmallFilesShareAShard()
    {
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");
        var plan = ShardPlanner.Plan(a + b, a.Length + b.Length, 4);

        Assert.Single(plan.Shards);
        Assert.Equal(2, plan.Shards[0].Files.Count);
        Assert.Equal(a + b, plan.Shards[0].DiffText);
    }

    [Fact]
    public void Plan_OversizeFile_GetsItsOwnShard_NeverSplit()
    {
        var big = Block("src/big.cs", 100); // far over budget
        var small = Block("src/small.cs", 2);
        var plan = ShardPlanner.Plan(big + small, 100, 4);

        Assert.False(plan.Overflowed);
        Assert.Equal(2, plan.Shards.Count);
        Assert.Equal(["src/big.cs"], plan.Shards[0].Files);
        Assert.Equal(big, plan.Shards[0].DiffText); // intact, not truncated to the budget
        Assert.Equal(["src/small.cs"], plan.Shards[1].Files);
    }

    [Fact]
    public void Plan_CapOverflow_MarksOverflowed()
    {
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");
        var c = Block("src/c.cs");
        var plan = ShardPlanner.Plan(a + b + c, a.Length, 2);

        Assert.True(plan.Overflowed);
    }

    [Fact]
    public void Plan_IsDeterministic_OrderIndependentInput()
    {
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");
        var c = Block("src/c.cs", 50); // own shard (oversize)

        var forward = ShardPlanner.Plan(a + b + c, a.Length, 8);
        var shuffled = ShardPlanner.Plan(c + b + a, a.Length, 8);

        Assert.Equal(3, forward.Shards.Count);
        Assert.Equal(3, shuffled.Shards.Count);
        for (var i = 0; i < forward.Shards.Count; i++)
        {
            Assert.Equal(forward.Shards[i].Files, shuffled.Shards[i].Files);
            Assert.Equal(forward.Shards[i].DiffText, shuffled.Shards[i].DiffText);
        }
    }

    [Fact]
    public void Plan_NormalizesPathsForOrdering()
    {
        var a = Block("SRC/A.CS", 2);
        var b = Block("src/b.cs", 2);
        var plan = ShardPlanner.Plan(b + a, a.Length + b.Length, 4);

        // Sorted by normalized path (ordinal, case-insensitive): a before b regardless of input order.
        Assert.Single(plan.Shards);
        Assert.Equal("SRC/A.CS", plan.Shards[0].Files[0]);
        Assert.Equal("src/b.cs", plan.Shards[0].Files[1]);
    }

    [Fact]
    public void Plan_DropsDeletionOnlyBlock_NoReviewableAddedLines()
    {
        var gone = "diff --git a/src/gone.cs b/src/gone.cs\n--- a/src/gone.cs\n+++ /dev/null\n-whole file\n";
        var kept = Block("src/kept.cs", 2);
        var plan = ShardPlanner.Plan(gone + kept, 100, 4);

        Assert.Single(plan.Shards);
        Assert.Equal(["src/kept.cs"], plan.Shards[0].Files);
        Assert.Equal(kept, plan.Shards[0].DiffText);
    }

    [Fact]
    public void Plan_EmptyDiff_NoShards_LegacyPath()
    {
        var plan = ShardPlanner.Plan(string.Empty, 100, 4);

        Assert.False(plan.Overflowed);
        Assert.Empty(plan.Shards);
    }

    [Fact]
    public void Plan_QuotedGitPaths_AreResolvedLikeDiffIndex()
    {
        // git C-quotes paths containing spaces: "diff --git \"a/x y.cs\" \"b/x y.cs\"".
        const string file = "src/x y.cs";
        var block =
            "diff --git \"a/src/x y.cs\" \"b/src/x y.cs\"\n" +
            "--- \"a/src/x y.cs\"\n" +
            "+++ \"b/src/x y.cs\"\n" +
            "@@ -0,0 +1,1 @@\n" +
            "+content\n";
        var other = Block("src/a.cs");
        var plan = ShardPlanner.Plan(block + other, block.Length + other.Length, 4);

        Assert.False(plan.Overflowed);
        Assert.Single(plan.Shards);
        Assert.Equal(["src/a.cs", file], plan.Shards[0].Files);
    }

    [Fact]
    public void Plan_UnresolvableHunkBlock_Overflows_FailsClosedToLegacy()
    {
        // A hunk-bearing block whose file cannot be attributed must never be silently
        // dropped from the sharded review: the plan overflows so the caller takes the
        // legacy whole-diff path.
        // "diff --git" header whose b-side cannot be resolved and no +++ line to rescue
        // it: added content that must force the fail-closed legacy path.
        var a = Block("src/a.cs");
        var mystery = "diff --git a/orphan.cs\n@@ -0,0 +1,1 @@\n+orphan\n";
        var plan = ShardPlanner.Plan(a + mystery, 10_000, 4);

        Assert.True(plan.Overflowed);
        Assert.Empty(plan.Shards);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_RejectsDegenerateArguments(int maxShards)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShardPlanner.Plan("x", 100, maxShards));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShardPlanner.Plan("x", 0, 4));
    }
}
