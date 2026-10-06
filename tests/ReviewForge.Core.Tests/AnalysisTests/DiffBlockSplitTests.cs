using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests.AnalysisTests;

public sealed class DiffBlockSplitTests
{
    private static string Block(string file, int bodyLines = 3) =>
        $"diff --git a/{file} b/{file}\n--- a/{file}\n+++ b/{file}\n" +
        string.Concat(Enumerable.Repeat($"+line in {file}\n", bodyLines));

    [Fact]
    public void Split_MultiFile_ReturnsOneBlockPerFile()
    {
        var a = Block("src/a.cs");
        var b = Block("src/b.cs");

        var blocks = DiffBlockSplit.Split(a + b);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("src/a.cs", blocks[0].File);
        Assert.Equal(a, blocks[0].Text);
        Assert.False(blocks[0].IsDeletion);
        Assert.Equal("src/b.cs", blocks[1].File);
        Assert.Equal(b, blocks[1].Text);
    }

    [Fact]
    public void Split_EmptyDiff_ReturnsNoBlocks()
    {
        var blocks = DiffBlockSplit.Split("");

        Assert.Empty(blocks);
    }

    [Fact]
    public void Split_CQuotedPaths_ResolvesFile()
    {
        var diff = "diff --git \"a/src/sp ace.cs\" \"b/src/sp ace.cs\"\n"
            + "--- \"a/src/sp ace.cs\"\n+++ \"b/src/sp ace.cs\"\n+added\n";

        var blocks = DiffBlockSplit.Split(diff);

        var block = Assert.Single(blocks);
        Assert.Equal("src/sp ace.cs", block.File);
        Assert.Equal(diff, block.Text);
    }

    [Fact]
    public void Split_DeletionBlock_RetainedWithFlag()
    {
        var del = "diff --git a/src/old.cs b/src/old.cs\ndeleted file mode 100644\n"
            + "--- a/src/old.cs\n+++ /dev/null\n-removed line\n";
        var keep = Block("src/keep.cs");

        var blocks = DiffBlockSplit.Split(del + keep);

        Assert.Equal(2, blocks.Count);
        Assert.Equal("src/old.cs", blocks[0].File);
        Assert.True(blocks[0].IsDeletion);
        Assert.Equal(del, blocks[0].Text);
        Assert.Equal("src/keep.cs", blocks[1].File);
        Assert.False(blocks[1].IsDeletion);
    }

    [Fact]
    public void Split_Preamble_CarriesNullFile()
    {
        var preamble = "Some header line\nAnother header\n";
        var a = Block("src/a.cs");

        var blocks = DiffBlockSplit.Split(preamble + a);

        Assert.Equal(2, blocks.Count);
        Assert.Null(blocks[0].File);
        Assert.Equal(preamble, blocks[0].Text);
        Assert.Equal("src/a.cs", blocks[1].File);
    }


    [Fact]
    public void Split_TrailingNewline_NotAnExtraBlock()
    {
        var a = Block("src/a.cs");

        var blocks = DiffBlockSplit.Split(a);

        Assert.Single(blocks);
    }
}
