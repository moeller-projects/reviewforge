using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public class DedupeKeyTests
{
    [Fact]
    public void Key_is_stable_and_line_independent()
    {
        var a = DedupeKey.Compute("null-deref", "src/Foo.cs", "return x.Value;");
        var b = DedupeKey.Compute("null-deref", "src/Foo.cs", "return x.Value;");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
    }

    [Fact]
    public void Key_normalizes_casing_whitespace_and_slashes()
    {
        var a = DedupeKey.Compute("NULL-DEREF", "\\src\\Foo.cs", "return   x.Value;");
        var b = DedupeKey.Compute("null-deref", "/src/foo.cs", "return x.value;");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Key_differs_per_rule_file_snippet()
    {
        var baseKey = DedupeKey.Compute("r", "f", "s");
        Assert.NotEqual(baseKey, DedupeKey.Compute("r2", "f", "s"));
        Assert.NotEqual(baseKey, DedupeKey.Compute("r", "f2", "s"));
        Assert.NotEqual(baseKey, DedupeKey.Compute("r", "f", "s2"));
    }

    [Fact]
    public void Null_snippet_is_dash()
    {
        Assert.Equal("-", DedupeKey.NormalizeSnippet(null));
        Assert.Equal(DedupeKey.Compute("r", "f", null), DedupeKey.Compute("r", "f", null));
    }
}

public class DiffIndexTests
{
    private const string Diff = """
                                diff --git a/src/A.cs b/src/A.cs
                                --- a/src/A.cs
                                +++ b/src/A.cs
                                @@ -1,2 +10,3 @@
                                 context
                                +added
                                @@ -20,1 +40,1 @@
                                -old
                                +new
                                diff --git a/src/Gone.cs b/src/Gone.cs
                                --- a/src/Gone.cs
                                +++ /dev/null
                                @@ -1,1 +0,0 @@
                                -bye
                                """;

    [Fact]
    public void Parses_files_and_added_ranges()
    {
        var index = DiffIndex.Parse(Diff);
        Assert.Contains("src/A.cs", index.Files);
        Assert.True(index.Contains("src/A.cs", 11));
        Assert.True(index.Contains("src/A.cs", 40));
        Assert.False(index.Contains("src/A.cs", 10)); // unchanged context
        Assert.False(index.Contains("src/A.cs", 12)); // unchanged context
        Assert.False(index.Contains("src/A.cs", 13));
        Assert.False(index.Contains("src/A.cs", 1));
    }

    [Fact]
    public void Deleted_files_are_not_indexed()
    {
        var index = DiffIndex.Parse(Diff);
        Assert.DoesNotContain("src/Gone.cs", index.Files);
        Assert.False(index.Contains("src/Gone.cs", 1));
    }

    [Fact]
    public void Normalizes_separators_and_leading_slash()
    {
        var index = DiffIndex.Parse(Diff);
        Assert.True(index.Contains("\\src\\A.cs", 11));
        Assert.True(index.Contains("/src/A.cs", 11));
    }

    [Fact]
    public void Unknown_file_is_false()
        => Assert.False(DiffIndex.Parse(Diff).Contains("nope.cs", 1));

    [Fact]
    public void Hunk_without_count_means_one_line()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -1 +7 @@\n+x\n");
        Assert.True(index.Contains("f.cs", 7));
        Assert.False(index.Contains("f.cs", 8));
    }

    [Fact]
    public void Zero_count_hunk_adds_nothing()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -1,0 +5,0 @@\n");
        Assert.False(index.Contains("f.cs", 5));
    }

    [Fact]
    public void Does_not_treat_added_diff_text_as_a_file_header()
    {
        var index = DiffIndex.Parse(
            "+++ b/changed.md\n@@ -0,0 +1,1 @@\n+++ b/modules/untouched/Model.cs\n");

        Assert.DoesNotContain("modules/untouched/Model.cs", index.Files);
        Assert.True(index.Contains("changed.md", 1));
    }
}

public class AnchorResolverTests
{
    private static RichFinding Finding(string? snippet, int line = 2)
        => new()
        {
            RuleId = "r",
            Title = "t",
            Severity = "high",
            Category = "bug",
            Description = "d",
            Snippet = snippet,
            Anchor = new FindingAnchor("f.cs", line, line),
        };

    [Fact]
    public void Null_anchor_is_verified_as_pr_level()
    {
        var finding = Finding("x");
        finding.Anchor = null;
        var (result, anchor) = AnchorResolver.Resolve(finding, ["a"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
        Assert.Null(anchor);
    }

    [Fact]
    public void Missing_snippet_trusts_anchor()
    {
        var (result, anchor) = AnchorResolver.Resolve(Finding(null), ["unrelated"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
        Assert.Equal(2, anchor!.StartLine);
    }

    [Fact]
    public void Snippet_on_same_line_verifies()
    {
        var (result, _) = AnchorResolver.Resolve(Finding("beta"), ["alpha", "beta", "gamma"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
    }

    [Fact]
    public void Snippet_on_other_line_reanchors()
    {
        var (result, anchor) = AnchorResolver.Resolve(Finding("gamma", line: 1), ["alpha", "beta", "gamma"]);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, result);
        Assert.Equal(3, anchor!.StartLine);
        Assert.Equal(3, anchor.EndLine);
    }

    [Fact]
    public void Whitespace_insensitive_matching()
    {
        var (result, _) = AnchorResolver.Resolve(Finding("return   x", line: 2), ["unrelated", "return x"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
    }

    [Fact]
    public void Multi_line_snippet_reanchors_as_a_range()
    {
        var finding = Finding("first line\nsecond   line", line: 1);

        var (result, anchor) = AnchorResolver.Resolve(
            finding,
            ["unrelated", "first line", "second line", "after"]);

        Assert.Equal(AnchorResolver.Resolution.Reanchored, result);
        Assert.Equal(2, anchor!.StartLine);
        Assert.Equal(3, anchor.EndLine);
    }

    [Fact]
    public void Missing_snippet_text_is_unverifiable()
    {
        var (result, _) = AnchorResolver.Resolve(Finding("not-present"), ["alpha"]);
        Assert.Equal(AnchorResolver.Resolution.Unverifiable, result);
    }
}