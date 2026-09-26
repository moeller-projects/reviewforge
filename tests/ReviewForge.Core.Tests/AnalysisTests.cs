using ReviewForge.Core.Analysis;
using ReviewForge.Core.Domain;
using ReviewForge.Core.Ports;
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

public class DiffPathParserTests
{
    [Theory]
    // Unquoted fast path.
    [InlineData("diff --git a/x.cs b/y.cs", "y.cs")]
    // core.quotePath: non-ASCII path octal-escaped, both sides quoted.
    [InlineData("diff --git \"a/caf\\303\\251.cs\" \"b/caf\\303\\251.cs\"", "café.cs")]
    // Mixed quoting: bare old side, quoted new side with an escaped quote in the name.
    [InlineData("diff --git a/x.cs \"b/we\\\"ird.cs\"", "we\"ird.cs")]
    // Quoted because of spaces; " b/" appears inside the path itself.
    [InlineData("diff --git \"a/pre b/post.cs\" \"b/pre b/post.cs\"", "pre b/post.cs")]
    // Escaped backslash then an escaped tab.
    [InlineData("diff --git \"a/d\\\\r\\t.cs\" \"b/d\\\\r\\t.cs\"", "d\\r\t.cs")]
    public void DiffGitNewPath_decodes_quoted_and_bare_paths(string line, string expected)
        => Assert.Equal(expected, DiffIndex.DiffGitNewPath(line));

    [Theory]
    [InlineData("diff --git a/x.cs")]                       // missing new side
    [InlineData("diff --git a/x.cs b/")]                    // empty new path
    [InlineData("diff --git a/x.cs b/y.cs trailing")]       // bare token may not contain spaces
    [InlineData("diff --git \"a/x.cs\" \"b/bad\\q.cs\"")]   // invalid escape
    [InlineData("diff --git \"a/x.cs\" \"b/unterminated")]  // unterminated quote
    [InlineData("diff --git \"a/x.cs\" \"x.cs\"")]          // missing b/ prefix
    [InlineData("not a diff header at all")]               // wrong prefix
    [InlineData("diff --git \"unterminated b/y.cs\"")]     // old-side token unterminated
    [InlineData("diff --git \"unterminated")]            // no closing quote at all in the line
    public void DiffGitNewPath_rejects_malformed_headers(string line)
        => Assert.Null(DiffIndex.DiffGitNewPath(line));

    [Theory]
    [InlineData("Binary files a/x.png b/y.png differ")]               // no " and " separator
    [InlineData("not a binary header")]                               // wrong prefix
    [InlineData("Binary files \"unterminated and b/y.png differ")]    // first token unterminated
    [InlineData("Binary files a/x.png and ")]                         // empty new side
    [InlineData("Binary files a/x.png and \"b\\q.png\" differ")]      // invalid escape in new token
    public void BinaryNewPath_rejects_malformed_headers(string line)
        => Assert.Null(DiffIndex.BinaryNewPath(line));

    [Fact]
    public void BinaryNewPath_decodes_quoted_paths()
    {
        Assert.Equal("lo go.png", DiffIndex.BinaryNewPath("Binary files \"a/lo go.png\" and \"b/lo go.png\" differ"));
        Assert.Equal("x.png", DiffIndex.BinaryNewPath("Binary files \"a/x.png\" and \"b/x.png\" differ"));
        Assert.Null(DiffIndex.BinaryNewPath("Binary files \"a/x.png\" and \"b/x.png\" trailing")); // junk after quote
    }

    [Theory]
    // git's named escapes besides \t (quote.c): bell backspace formfeed newline carriage-return vtab
    [InlineData("\"b/a\\ab\\bc.cs\"", "b/a\ab\bc.cs")]
    [InlineData("\"b/a\\fb\\nc.cs\"", "b/a\fb\nc.cs")]
    [InlineData("\"b/a\\rb\\vc.cs\"", "b/a\rb\vc.cs")]
    [InlineData("plain.cs", "plain.cs")]
    [InlineData("\"b/caf\\303\\251.cs\"", "b/café.cs")]
    [InlineData("\"b/café.cs\"", "b/café.cs")]       // quoted, no escapes — fast path
    [InlineData("\"b/a\\tb.cs\"", "b/a\tb.cs")]
    [InlineData("\"b/a\\\\b.cs\"", "b/a\\b.cs")]
    public void TryDecodeToken_roundtrips_git_quoting(string token, string expected)
    {
        Assert.True(DiffPathParser.TryDecodeToken(token, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]              // empty
    [InlineData("\"unterminated")]// unterminated quote
    [InlineData("\"a\\q\"")]      // invalid escape
    [InlineData("\"a\" junk")]    // trailing junk after closing quote
    public void TryDecodeToken_rejects_malformed_tokens(string token)
        => Assert.False(DiffPathParser.TryDecodeToken(token, out _));

    [Fact]
    public void TryReadToken_respects_quoted_segments()
    {
        Assert.True(DiffPathParser.TryReadToken("\"a/x y.cs\" rest", out var token, out var consumed));
        Assert.Equal("\"a/x y.cs\"", token.ToString());
        Assert.Equal(10, consumed);

        Assert.True(DiffPathParser.TryReadToken("a/x.cs rest", out var bare, out var bareConsumed));
        Assert.Equal("a/x.cs", bare.ToString());
        Assert.Equal(6, bareConsumed);

        Assert.False(DiffPathParser.TryReadToken("\"unterminated", out _, out _));
        Assert.False(DiffPathParser.TryReadToken("", out _, out _));
        Assert.False(DiffPathParser.TryReadToken(" ", out _, out _)); // bare token of zero length
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

    [Fact]
    public void Parse_registers_binary_file_as_non_reviewable()
    {
        var index = DiffIndex.Parse("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n");

        Assert.Empty(index.Files);
        Assert.Equal(DiffEntryKind.Binary, index.NonReviewableFiles["logo.png"]);
    }

    [Fact]
    public void Truncated_diff_is_explicitly_non_reviewable()
    {
        var index = DiffIndex.Parse(
            "diff --git a/src/Large.cs b/src/Large.cs\n" +
            "--- a/src/Large.cs\n+++ b/src/Large.cs\n" +
            "…[file diff skipped — 300 bytes exceeds the per-file budget; use repo_read_file]\n");

        Assert.DoesNotContain("src/Large.cs", index.Files);
        Assert.Equal(DiffEntryKind.Truncated, index.NonReviewableFiles["src/Large.cs"]);
        Assert.False(index.Contains("src/Large.cs", 1));
    }

    [Fact]
    public void Parse_registers_mode_only_change()
    {
        var index = DiffIndex.Parse("diff --git a/run.sh b/run.sh\nold mode 100644\nnew mode 100755\n");

        Assert.Empty(index.Files);
        Assert.Equal(DiffEntryKind.ModeOnly, index.NonReviewableFiles["run.sh"]);
    }

    [Fact]
    public void Parse_registers_content_free_rename()
    {
        var index = DiffIndex.Parse(
            "diff --git a/a.cs b/b.cs\nsimilarity index 100%\nrename from a.cs\nrename to b.cs\n");

        Assert.Empty(index.Files);
        Assert.Equal(DiffEntryKind.RenameOnly, index.NonReviewableFiles["b.cs"]);
    }

    [Fact]
    public void Parse_keeps_rename_with_content_as_text()
    {
        var index = DiffIndex.Parse(
            "diff --git a/a.cs b/b.cs\nsimilarity index 90%\nrename from a.cs\nrename to b.cs\n" +
            "--- a/a.cs\n+++ b/b.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n");

        Assert.Contains("b.cs", index.Files);
        Assert.DoesNotContain("b.cs", index.NonReviewableFiles.Keys);
    }

    [Fact]
    public void Parse_text_file_followed_by_binary_keeps_both()
    {
        var index = DiffIndex.Parse(
            "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1,1 @@\n+changed\n" +
            "diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n");

        Assert.Contains("a.cs", index.Files);
        Assert.True(index.Contains("a.cs", 1));
        Assert.Equal(DiffEntryKind.Binary, index.NonReviewableFiles["logo.png"]);
    }

    [Fact]
    public void Parse_handles_no_newline_marker_inside_hunk()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -0,0 +1,3 @@\n+x\n\\ No newline at end of file\n+y\n+z\n");

        Assert.True(index.Contains("f.cs", 1));
        Assert.True(index.Contains("f.cs", 2));
        Assert.True(index.Contains("f.cs", 3));
    }

    [Fact]
    public void Contiguous_added_lines_coalesce_into_one_range()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"+line{i}");
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -0,0 +1,100 @@\n" + string.Join('\n', lines) + "\n");

        Assert.Equal(1, index.RangeCount("f.cs"));
        Assert.True(index.Contains("f.cs", 1));
        Assert.True(index.Contains("f.cs", 50));
        Assert.True(index.Contains("f.cs", 100));
        Assert.False(index.Contains("f.cs", 101));
    }

    [Fact]
    public void Separate_hunks_stay_separate_ranges()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -0,0 +1,2 @@\n+a\n+b\n@@ -0,0 +5,2 @@\n+e\n+f\n");

        Assert.Equal(2, index.RangeCount("f.cs"));
        Assert.True(index.Contains("f.cs", 1));
        Assert.True(index.Contains("f.cs", 2));
        Assert.False(index.Contains("f.cs", 3)); // gap between hunks
        Assert.False(index.Contains("f.cs", 4));
        Assert.True(index.Contains("f.cs", 5));
        Assert.True(index.Contains("f.cs", 6));
    }

    [Fact]
    public void Hunk_like_content_line_is_not_a_header()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -0,0 +1,3 @@\n+x\n @@ -1 +2 @@\n+y\n");

        Assert.True(index.Contains("f.cs", 1));   // +x
        Assert.False(index.Contains("f.cs", 2));  // the "@@ -1 +2 @@" context line
        Assert.True(index.Contains("f.cs", 3));   // +y
    }

    [Fact]
    public void Contains_uses_sorted_ranges()
    {
        var hunks = Enumerable.Range(0, 50).Select(h => $"@@ -0,0 +{h * 10 + 1},1 @@\n+line{h}");
        var index = DiffIndex.Parse("+++ b/f.cs\n" + string.Join('\n', hunks) + "\n");

        Assert.True(index.Contains("f.cs", 1));    // first hunk
        Assert.True(index.Contains("f.cs", 491));  // last hunk (49*10+1)
        Assert.False(index.Contains("f.cs", 500));
    }

    [Fact]
    public void Skip_note_header_registers_file_with_no_lines()
    {
        var index = DiffIndex.Parse(
            "diff --git a/big.cs b/big.cs\n--- a/big.cs\n+++ b/big.cs\n" +
            "…[file diff skipped — 999999 bytes exceeds the per-file budget; use repo_read_file]\n");

        Assert.DoesNotContain("big.cs", index.Files);
        Assert.Equal(DiffEntryKind.Truncated, index.NonReviewableFiles["big.cs"]);
    }

    [Fact]
    public void Hunk_header_parser_rejects_malformed_headers()
    {
        Assert.False(DiffIndex.TryParseHunkNewStart("@@ -1 @@", out _));    // no '+'
        Assert.False(DiffIndex.TryParseHunkNewStart("@@ -1 + @@", out _));  // '+' without digits
        Assert.False(DiffIndex.TryParseHunkNewStart("@@ -1 +1,5", out _));  // ',' without closing '@@'
        Assert.False(DiffIndex.TryParseHunkNewStart("@@ -1 +1", out _));    // no closing '@@'
        Assert.True(DiffIndex.TryParseHunkNewStart("@@ -1 +7 @@", out var start));
        Assert.Equal(7, start);
        Assert.True(DiffIndex.TryParseHunkNewStart("@@ -1,3 +7,2 @@", out var startWithCount));
        Assert.Equal(7, startWithCount);
    }

    [Fact]
    public void Hunk_ends_on_unexpected_line()
    {
        var index = DiffIndex.Parse("+++ b/f.cs\n@@ -0,0 +1,2 @@\n+x\nUNEXPECTED\n+y\n");

        Assert.True(index.Contains("f.cs", 1));   // +x
        Assert.False(index.Contains("f.cs", 2));  // +y follows an unexpected line — hunk ended
    }

    [Fact]
    public void Parse_registers_quoted_paths_and_flushes_previous_section()
    {
        var index = DiffIndex.Parse(
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,2 @@\n keep\n+added\n" +
            "diff --git \"a/caf\\303\\251.cs\" \"b/caf\\303\\251.cs\"\n" +
            "--- \"a/caf\\303\\251.cs\"\n+++ \"b/caf\\303\\251.cs\"\n@@ -0,0 +1,1 @@\n+payload\n");

        Assert.Contains("src/A.cs", index.Files);
        Assert.True(index.Contains("src/A.cs", 2)); // +added closed the quoted section cleanly
        Assert.True(index.Contains("café.cs", 1));  // non-ASCII name decoded
        Assert.False(index.Contains("café.cs", 2));
    }

    [Fact]
    public void Parse_quoted_path_with_escaped_quote_and_space()
    {
        var index = DiffIndex.Parse(
            "diff --git \"a/we\\\"ird dir/x.cs\" \"b/we\\\"ird dir/x.cs\"\n" +
            "--- \"a/we\\\"ird dir/x.cs\"\n+++ \"b/we\\\"ird dir/x.cs\"\n@@ -0,0 +1,1 @@\n+x\n");

        Assert.True(index.Contains("we\"ird dir/x.cs", 1));
    }

    [Fact]
    public void Parse_malformed_quoted_header_fails_closed_without_throwing()
    {
        var index = DiffIndex.Parse(
            "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n@@ -0,0 +1,1 @@\n+x\n" +
            "diff --git \"a/bad\\q.cs\" \"b/bad\\q.cs\"\n--- \"a/bad\\q.cs\"\n+++ \"b/bad\\q.cs\"\n@@ -0,0 +1,1 @@\n+y\n");

        Assert.Contains("a.cs", index.Files);
        Assert.DoesNotContain("bad\\q.cs", index.Files); // undecodable file is not indexed
        Assert.Empty(index.NonReviewableFiles);          // ...nor whitelisted — scope guard throws
    }

    [Fact]
    public void Binary_new_path_rejects_malformed_marker()
    {
        Assert.Null(DiffIndex.BinaryNewPath("Binary files a/x b/y differ")); // no " and b/"
        Assert.Equal("logo.png", DiffIndex.BinaryNewPath("Binary files a/logo.png and b/logo.png differ"));
    }
}

public class DiffExclusionsTests
{
    [Fact]
    public void Glob_matches_lockfiles_and_generated_code()
    {
        var globs = DiffBudget.Default.ExcludeGlobs;
        Assert.True(DiffExclusions.IsExcluded("src/app/package-lock.json", globs));
        Assert.True(DiffExclusions.IsExcluded("obj/Foo.Designer.cs", globs));
        Assert.True(DiffExclusions.IsExcluded("wwwroot/site.min.js", globs));
        Assert.False(DiffExclusions.IsExcluded("src/Foo.cs", globs));
        Assert.False(DiffExclusions.IsExcluded("src/Design.cs", globs));
    }

    [Fact]
    public void Glob_star_does_not_cross_segments()
    {
        Assert.True(DiffExclusions.IsExcluded("a/b/c.min.js", ["**/*.min.js"]));
        Assert.True(DiffExclusions.IsExcluded("c.min.js", ["**/*.min.js"]));
        Assert.True(DiffExclusions.IsExcluded("root.js", ["*.js"]));
        Assert.False(DiffExclusions.IsExcluded("a/root.js", ["*.js"]));
    }

    [Fact]
    public void Glob_trailing_star_matches_zero_or_more()
    {
        Assert.True(DiffExclusions.IsExcluded("foo", ["foo*"]));      // star matches zero chars
        Assert.True(DiffExclusions.IsExcluded("foobar", ["foo*"]));   // star matches more
        Assert.False(DiffExclusions.IsExcluded("barfoo", ["foo*"]));  // prefix mismatch
    }

    [Fact]
    public void Glob_repeated_double_stars_have_bounded_matching()
    {
        var path = string.Join('/', Enumerable.Repeat("segment", 32));
        var pattern = string.Join('/', Enumerable.Repeat("**", 16)) + "/never";

        Assert.False(DiffExclusions.IsExcluded(path, [pattern]));
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
        var (result, _) = AnchorResolver.Resolve(Finding("return beta"), ["alpha", "return beta", "gamma"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
    }

    [Fact]
    public void Snippet_on_other_line_reanchors()
    {
        var (result, anchor) = AnchorResolver.Resolve(Finding("return gamma", line: 1), ["alpha", "beta", "return gamma"]);
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

    [Fact]
    public void Resolve_TrivialBraceSnippet_ReturnsWeakSnippet()
    {
        var (result, anchor) = AnchorResolver.Resolve(Finding("}", line: 50), ["{", "}", "x"]);
        Assert.Equal(AnchorResolver.Resolution.WeakSnippet, result);
        Assert.Equal(50, anchor!.StartLine);
    }

    [Fact]
    public void Resolve_BlankOnlySnippet_TreatedAsNoSnippet()
    {
        var (result, anchor) = AnchorResolver.Resolve(Finding("\n  \n", line: 7), ["alpha", "beta"]);
        Assert.Equal(AnchorResolver.Resolution.Verified, result);
        Assert.Equal(7, anchor!.StartLine);
    }

    [Fact]
    public void Resolve_SnippetWithBlankLines_BlankLinesIgnored()
    {
        var finding = Finding("alpha one\n\nbeta two", line: 1);
        var (result, anchor) = AnchorResolver.Resolve(finding, ["unrelated", "alpha one", "beta two", "after"]);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, result);
        Assert.Equal(2, anchor!.StartLine);
        Assert.Equal(3, anchor.EndLine);
    }

    [Fact]
    public void Resolve_MultipleMatches_PrefersNearestToStatedLine()
    {
        var file = new[] { "one", "return beta", "three", "four", "five", "six", "seven", "eight", "return beta", "ten" };

        var (r1, a1) = AnchorResolver.Resolve(Finding("return beta", line: 8), file);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, r1);
        Assert.Equal(9, a1!.StartLine);

        var (r2, a2) = AnchorResolver.Resolve(Finding("return beta", line: 1), file);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, r2);
        Assert.Equal(2, a2!.StartLine);
    }

    [Fact]
    public void Resolve_MultipleMatches_Tie_PrefersLowerLine()
    {
        var file = new[] { "one", "two", "return beta", "four", "five", "six", "return beta", "eight" };
        var (result, anchor) = AnchorResolver.Resolve(Finding("return beta", line: 5), file);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, result);
        Assert.Equal(3, anchor!.StartLine);
    }

    [Fact]
    public void Resolve_TwoShortLines_AboveFloorViaMultiLineRule()
    {
        var finding = Finding("}\n})", line: 1);
        var (result, anchor) = AnchorResolver.Resolve(finding, ["unrelated", "}", "})"]);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, result);
        Assert.Equal(2, anchor!.StartLine);
        Assert.Equal(3, anchor.EndLine);
    }

    [Fact]
    public void PreparedFile_resolves_multiple_findings()
    {
        var file = new AnchorResolver.PreparedFile(["alpha", "return beta", "return gamma"]);
        Assert.Equal(3, file.Normalized.Length);
        Assert.Equal("alpha", file.Normalized[0]);

        var (r1, _) = AnchorResolver.Resolve(Finding("return beta"), file);
        Assert.Equal(AnchorResolver.Resolution.Verified, r1);

        var (r2, a2) = AnchorResolver.Resolve(Finding("return gamma", line: 1), file);
        Assert.Equal(AnchorResolver.Resolution.Reanchored, r2);
        Assert.Equal(3, a2!.StartLine);
    }
}

public class RepoPathTests
{
    [Theory]
    [InlineData(@"src\Foo\Bar.cs", "src/Foo/Bar.cs")]
    [InlineData("/src/Foo.cs", "src/Foo.cs")]
    [InlineData(@"\\server\share\f.cs", "server/share/f.cs")]
    [InlineData("already/fine.cs", "already/fine.cs")]
    public void Normalize_canonicalizes(string input, string expected)
        => Assert.Equal(expected, RepoPath.Normalize(input));

    [Fact]
    public void NormalizeKey_lowercases()
        => Assert.Equal("src/foo.cs", RepoPath.NormalizeKey(@"\Src\Foo.cs"));
}