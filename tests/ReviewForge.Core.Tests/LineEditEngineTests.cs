using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public class HashLineTests
{
    [Fact]
    public void Of_is_stable_and_8_lowercase_hex()
    {
        var h1 = HashLine.Of("var x = 1;");
        var h2 = HashLine.Of("var x = 1;");
        Assert.Equal(h1, h2);
        Assert.Equal(HashLine.HashLength, h1.Length);
        Assert.Matches("^[0-9a-f]{8}$", h1);
    }

    [Fact]
    public void Of_normalizes_trailing_whitespace_terminator_and_bom()
    {
        Assert.Equal(HashLine.Of("content"), HashLine.Of("content   "));
        Assert.Equal(HashLine.Of("content"), HashLine.Of("content\r\n"));
        Assert.Equal(HashLine.Of("content"), HashLine.Of("content\n"));
        Assert.Equal(HashLine.Of("content"), HashLine.Of("﻿content"));
    }

    [Fact]
    public void Of_preserves_leading_indentation()
        => Assert.NotEqual(HashLine.Of("content"), HashLine.Of("  content"));

    [Fact]
    public void Of_distinguishes_content()
        => Assert.NotEqual(HashLine.Of("a"), HashLine.Of("b"));

    [Fact]
    public void DetectNewLine_prefers_crlf()
    {
        Assert.Equal("\r\n", HashLine.DetectNewLine(["a\r\n", "b\r\n", "c"]));
        Assert.Equal("\n", HashLine.DetectNewLine(["a\n", "b\n", "c"]));
        Assert.Equal("\n", HashLine.DetectNewLine(["a", "b"]));
        Assert.Equal("\r\n", HashLine.DetectNewLine(["a\r\n", "b\n"]));
    }
}

public class LineEditEngineTests
{
    private static string H(string line) => HashLine.Of(line);

    [Fact]
    public void Single_line_replace_by_hash()
    {
        var lines = new[] {"one", "two", "three"};
        var result = LineEditEngine.Apply(lines, [new LineEdit(H("two"), null, null, null, "TWO")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["one", "TWO", "three"], next);
        Assert.Equal(new EditOutcome(2, 2, 1), result.Outcomes[0]);
        Assert.NotNull(result.NewFileHash);
    }

    [Fact]
    public void Empty_replacement_deletes_the_line()
    {
        var lines = new[] {"one", "two", "three"};
        var result = LineEditEngine.Apply(lines, [new LineEdit(H("two"), null, null, null, "")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["one", "three"], next);
        Assert.Equal(0, result.Outcomes[0].ReplacementLineCount);
    }

    [Fact]
    public void Replacement_may_change_line_count()
    {
        var lines = new[] {"one", "two", "three"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit(H("two"), null, null, null, "a\nb\nc")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["one", "a", "b", "c", "three"], next);
        Assert.Equal(3, result.Outcomes[0].ReplacementLineCount);
    }

    [Fact]
    public void Trailing_newline_in_replacement_is_not_an_extra_line()
    {
        var lines = new[] {"one"};
        var result = LineEditEngine.Apply(lines, [new LineEdit(H("one"), null, null, null, "x\n")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["x"], next);
    }

    [Fact]
    public void Hash_not_present_fails_atomically()
    {
        var lines = new[] {"one", "two"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit("deadbeef", null, null, null, "x")], out var next);
        Assert.False(result.Success);
        Assert.Contains("hash deadbeef not present", result.Error);
        Assert.Contains("re-read and retry", result.Error);
        Assert.Empty(next);
    }

    [Fact]
    public void Ambiguous_hash_without_hint_lists_matching_lines()
    {
        var lines = new[] {"dup", "mid", "dup"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit(H("dup"), null, null, null, "x")], out _);
        Assert.False(result.Success);
        Assert.Equal($"hash {H("dup")} matches lines 1, 3 — add fromLine/toLine to disambiguate", result.Error);
    }

    [Fact]
    public void Ambiguous_hash_with_agreeing_hint_resolves()
    {
        var lines = new[] {"dup", "mid", "dup"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit(H("dup"), null, 3, null, "x")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["dup", "mid", "x"], next);
    }

    [Fact]
    public void Ambiguous_hash_with_wrong_hint_fails()
    {
        var lines = new[] {"dup", "mid", "dup"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit(H("dup"), null, 2, null, "x")], out _);
        Assert.False(result.Success);
        Assert.Equal($"hash {H("dup")} not at line 2 (found at 1)", result.Error);
    }

    [Fact]
    public void Explicit_line_range_without_hash()
    {
        var lines = new[] {"one", "two", "three"};
        var result = LineEditEngine.Apply(
            lines, [new LineEdit(null, null, 1, 2, "x")], out var next);
        Assert.True(result.Success);
        Assert.Equal(["x", "three"], next);
        Assert.Equal(new EditOutcome(1, 2, 1), result.Outcomes[0]);
    }

    [Fact]
    public void Edit_without_hash_or_range_fails()
    {
        var result = LineEditEngine.Apply(["one"], [new LineEdit(null, null, null, null, "x")], out _);
        Assert.False(result.Success);
        Assert.Contains("fromHash or an explicit fromLine/toLine", result.Error);
    }

    [Fact]
    public void Invalid_range_fails()
    {
        var result = LineEditEngine.Apply(["one"], [new LineEdit(null, null, 5, 9, "x")], out _);
        Assert.False(result.Success);
        Assert.Equal("invalid line range 5-9", result.Error);
    }

    [Fact]
    public void Reversed_range_fails()
    {
        var result = LineEditEngine.Apply(["one", "two"], [new LineEdit(null, null, 2, 1, "x")], out _);
        Assert.False(result.Success);
        Assert.Contains("invalid line range", result.Error);
    }

    [Fact]
    public void Overlapping_batch_is_rejected()
    {
        var lines = new[] {"a", "b", "c", "d"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("b"), H("c"), null, null, "x"), new LineEdit(H("c"), null, null, null, "y")],
            out _);
        Assert.False(result.Success);
        Assert.Equal("edits overlap at line 3 — merge or reorder", result.Error);
    }

    [Fact]
    public void Overlap_detected_after_resolution_against_original_lines()
    {
        // Two different hashes that resolve to the same line via hints would be caught,
        // but adjacent non-overlapping edits both apply cleanly bottom-up.
        var lines = new[] {"a", "b", "c", "d"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("a"), null, null, null, "x"), new LineEdit(H("d"), null, null, null, "y")],
            out var next);
        Assert.True(result.Success);
        Assert.Equal(["x", "b", "c", "y"], next);
    }

    [Fact]
    public void One_failing_edit_rejects_the_whole_batch()
    {
        var lines = new[] {"a", "b", "c"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("a"), null, null, null, "x"), new LineEdit("cafebabe", null, null, null, "y")],
            out var next);
        Assert.False(result.Success);
        Assert.Empty(next); // atomic: untouched
    }

    [Fact]
    public void Empty_edit_list_succeeds_with_identity()
    {
        var lines = new[] {"a", "b"};
        var result = LineEditEngine.Apply(lines, [], out var next);
        Assert.True(result.Success);
        Assert.Equal(lines, next);
        Assert.Empty(result.Outcomes);
    }

    [Fact]
    public void ToHash_extends_the_replaced_range()
    {
        var lines = new[] {"start", "middle", "end", "tail"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("start"), H("end"), null, null, "only")],
            out var next);
        Assert.True(result.Success);
        Assert.Equal(["only", "tail"], next);
        Assert.Equal(new EditOutcome(1, 3, 1), result.Outcomes[0]);
    }

    [Fact]
    public void Duplicate_to_hash_uses_to_line_hint()
    {
        var lines = new[] {"start", "middle", "end", "end", "tail"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("start"), H("end"), null, 4, "only")],
            out var next);

        Assert.True(result.Success);
        Assert.Equal(["only", "tail"], next);
        Assert.Equal(new EditOutcome(1, 4, 1), result.Outcomes[0]);
    }

    [Fact]
    public void End_hash_before_start_reports_ordering_error()
    {
        var lines = new[] {"start", "middle", "end"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("end"), H("start"), null, null, "only")],
            out _);

        Assert.False(result.Success);
        Assert.Equal($"hash {H("start")} must occur after start line 3", result.Error);
    }


    [Fact]
    public void Path_is_included_in_hash_absent_message()
    {
        var result = LineEditEngine.Apply(["a"], [new LineEdit("deadbeef", null, null, null, "x")], "src/Foo.cs", out _);
        Assert.Equal("hash deadbeef not present in src/Foo.cs — the file changed since your last read; re-read and retry", result.Error);
    }
    [Fact]
    public void Duplicate_to_hash_without_hint_requires_disambiguation()
    {
        var lines = new[] {"start", "end", "end"};
        var result = LineEditEngine.Apply(
            lines,
            [new LineEdit(H("start"), H("end"), null, null, "only")],
            out _);

        Assert.False(result.Success);
        Assert.Equal(
            $"hash {H("end")} matches lines 2, 3 — add fromLine/toLine to disambiguate",
            result.Error);
    }
}
