using ReviewForge.Core.Analysis;
using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

/// <summary>HashLineEditor against real temp dirs; mirrors RepoReadToolsTests conventions.</summary>
public sealed class HashLineEditorTests : IDisposable
{
    private readonly string _Root;

    public HashLineEditorTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_Root, "src"));
        File.WriteAllLines(Path.Combine(_Root, "src", "Foo.cs"), ["alpha", "beta", "gamma", "delta"]);
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private static string H(string line) => HashLine.Of(line);

    private HashLineEditor Editor(IReadOnlySet<string>? writable = null)
        => new(new RepoPathGuard(_Root), writable ?? new HashSet<string> { "src/Foo.cs" });

    private string FilePath => Path.Combine(_Root, "src", "Foo.cs");

    private string[] Current() => File.ReadAllLines(FilePath);

    [Fact]
    public void ReadFileWith_hashes_formats_lines()
    {
        var output = Editor().ReadFileWithHashes("src/Foo.cs");
        Assert.Contains($"1 {H("alpha")}: alpha", output);
        Assert.Contains($"4 {H("delta")}: delta", output);
    }

    [Fact]
    public void ReadFileWithHashes_marks_repeated_hashes_with_star()
    {
        File.WriteAllLines(FilePath, ["same", "other", "same"]);
        var output = Editor().ReadFileWithHashes("src/Foo.cs");
        Assert.Contains($"1 {H("same")}*: same", output);
        Assert.Contains($"3 {H("same")}*: same", output);
        Assert.Contains($"2 {H("other")}: other", output);
    }

    [Fact]
    public void ReadFileWithHashes_slices_and_marks_more()
    {
        var output = Editor().ReadFileWithHashes("src/Foo.cs", startLine: 3, maxLines: 1);
        Assert.Contains($"3 {H("gamma")}: gamma", output);
        Assert.Contains("more lines", output);
    }

    [Fact]
    public void ReadFileWithHashes_out_of_range_start()
    {
        Assert.Contains("file has 4 lines; startLine 99 is out of range",
            Editor().ReadFileWithHashes("src/Foo.cs", startLine: 99));
    }

    [Fact]
    public void ReadFileWithHashes_denies_escape()
        => Assert.Equal("access denied: ../evil", Editor().ReadFileWithHashes("../evil"));

    [Fact]
    public void ReadFileWithHashes_denies_secrets()
        => Assert.Equal("access denied: .env", Editor().ReadFileWithHashes(".env"));

    [Fact]
    public void ReadFileWithHashes_missing_file()
        => Assert.Equal("not found: src/nope.cs", Editor().ReadFileWithHashes("src/nope.cs"));

    [Fact]
    public void ReadFileWithHashes_refuses_binary()
    {
        File.WriteAllText(FilePath, "a\0b");
        Assert.Equal("refused: binary file", Editor().ReadFileWithHashes("src/Foo.cs"));
    }

    [Fact]
    public void EditFile_round_trip_updates_file_and_reports()
    {
        var result = Editor().EditFile("src/Foo.cs", [new LineEdit(H("beta"), null, null, null, "BETA")]);
        Assert.Equal(["alpha", "BETA", "gamma", "delta"], Current());
        Assert.Equal(
            $"applied 1 edits to src/Foo.cs{Environment.NewLine}" +
            $"  - lines 2 → 1 line (0 net){Environment.NewLine}" +
            $"new file hash: {HashLine.Of(string.Join('\n', Current()))}  (call read_file_with_hashes to verify){Environment.NewLine}",
            result);
    }

    [Fact]
    public void EditFile_success_message_for_delete_and_growth()
    {
        var result = Editor().EditFile("src/Foo.cs",
            [new LineEdit(H("gamma"), null, null, null, ""), new LineEdit(H("alpha"), null, null, null, "a1\na2")]);
        Assert.Contains("applied 2 edits to src/Foo.cs", result);
        Assert.Contains("  - lines 3 → deleted", result);
        Assert.Contains("  - lines 1 → 2 lines (+1 net)", result);
        Assert.Contains("new file hash: ", result);
    }

    [Fact]
    public void EditFile_empty_array_is_rejected()
    {
        Assert.Equal("no edits provided", Editor().EditFile("src/Foo.cs", []));
        Assert.Equal(["alpha", "beta", "gamma", "delta"], Current());
    }

    [Fact]
    public void EditFile_hash_absent_after_external_modification()
    {
        var editor = Editor();
        var staleHash = H("beta");
        File.WriteAllLines(FilePath, ["alpha", "CHANGED", "gamma", "delta"]);
        Assert.Equal(
            $"hash {staleHash} not present in src/Foo.cs — the file changed since your last read; re-read and retry",
            editor.EditFile("src/Foo.cs", [new LineEdit(staleHash, null, null, null, "x")]));
    }

    [Fact]
    public void EditFile_ambiguous_hash_requires_hint()
    {
        File.WriteAllLines(FilePath, ["dup", "mid", "dup", "last"]);
        Assert.Equal(
            $"hash {H("dup")} matches lines 1, 3 — add fromLine/toLine to disambiguate",
            Editor().EditFile("src/Foo.cs", [new LineEdit(H("dup"), null, null, null, "x")]));
    }

    [Fact]
    public void EditFile_wrong_hint_is_rejected()
    {
        File.WriteAllLines(FilePath, ["dup", "mid", "dup", "last"]);
        Assert.Equal(
            $"hash {H("dup")} not at line 2 (found at 1)",
            Editor().EditFile("src/Foo.cs", [new LineEdit(H("dup"), null, 2, null, "x")]));
    }

    [Fact]
    public void EditFile_overlapping_batch_is_atomic()
    {
        var before = File.ReadAllBytes(FilePath);
        var result = Editor().EditFile("src/Foo.cs",
            [new LineEdit(H("beta"), H("gamma"), null, null, "x"), new LineEdit(H("gamma"), null, null, null, "y")]);
        Assert.Equal("edits overlap at line 3 — merge or reorder", result);
        Assert.Equal(before, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void EditFile_escaping_path_is_denied()
    {
        Assert.Equal("access denied: ../../outside",
            Editor().EditFile("../../outside", [new LineEdit("a", null, null, null, "x")]));
    }

    [Fact]
    public void EditFile_outside_writable_set_is_denied()
    {
        var editor = new HashLineEditor(new RepoPathGuard(_Root), new HashSet<string> { "src/Other.cs" });
        Assert.Equal("access denied: src/Foo.cs is outside the writable set",
            editor.EditFile("src/Foo.cs", [new LineEdit(H("beta"), null, null, null, "x")]));
        Assert.Equal(["alpha", "beta", "gamma", "delta"], Current());
    }

    [Fact]
    public void EditFile_missing_file()
        => Assert.Equal("not found: src/ghost.cs",
            Editor(new HashSet<string> {"src/ghost.cs"}).EditFile(
                "src/ghost.cs", [new LineEdit("a", null, null, null, "x")]));

    [Fact]
    public void EditFile_refuses_mixed_line_endings()
    {
        File.WriteAllText(FilePath, "l1\nl2\r\nl3\nl4\r\n");
        Assert.Equal("refused: mixed line endings",
            Editor().EditFile("src/Foo.cs", [new LineEdit(H("l1"), null, null, null, "x")]));
    }

    [Fact]
    public void EditFile_preserves_crlf()
    {
        File.WriteAllText(FilePath, "one\r\ntwo\r\nthree\r\n");
        var result = Editor().EditFile("src/Foo.cs", [new LineEdit(H("two"), null, null, null, "TWO")]);
        Assert.Contains("applied 1 edits", result);
        Assert.Equal("one\r\nTWO\r\nthree\r\n", File.ReadAllText(FilePath));
    }

    [Fact]
    public void EditFile_line_count_change_tracked_in_session()
    {
        var editor = Editor();
        editor.EditFile("src/Foo.cs", [new LineEdit(H("beta"), null, null, null, "b1\nb2\nb3")]);
        var change = editor.GetSessionChange("src/Foo.cs");
        Assert.NotNull(change);
        Assert.Equal(2, change.Value.StartLine);
        Assert.Equal(4, change.Value.EndLine);
        Assert.Equal("b1\nb2\nb3", change.Value.Replacement);
    }

    [Fact]
    public void GetSessionChange_merges_disjoint_edits_into_contiguous_region()
    {
        var editor = Editor();
        editor.EditFile("src/Foo.cs", [new LineEdit(H("alpha"), null, null, null, "A")]);
        editor.EditFile("src/Foo.cs", [new LineEdit(H("delta"), null, null, null, "D")]);
        var change = editor.GetSessionChange("src/Foo.cs");
        Assert.NotNull(change);
        Assert.Equal(1, change.Value.StartLine);
        Assert.Equal(4, change.Value.EndLine);
        Assert.Equal("A\nbeta\ngamma\nD", change.Value.Replacement);
    }

    [Fact]
    public void GetSessionChange_null_when_no_edits()
        => Assert.Null(Editor().GetSessionChange("src/Foo.cs"));

    [Fact]
    public void ApplyRange_happy_path_with_drift_guard()
    {
        var editor = Editor();
        var rangeHash = HashLine.Of("beta\ngamma");
        var result = editor.ApplyRange("src/Foo.cs", 2, 3, "B\nG", rangeHash);
        Assert.True(result.Success);
        Assert.Equal(["alpha", "B", "G", "delta"], Current());
    }

    [Fact]
    public void ApplyRange_rejects_drifted_content()
    {
        var editor = Editor();
        File.WriteAllLines(FilePath, ["alpha", "CHANGED", "gamma", "delta"]);
        var result = editor.ApplyRange("src/Foo.cs", 2, 3, "B\nG", HashLine.Of("beta\ngamma"));
        Assert.False(result.Success);
        Assert.Equal("hash mismatch at src/Foo.cs:2-3 — re-read and retry", result.Error);
    }

    [Fact]
    public void ApplyRange_outside_writable_set()
    {
        var editor = new HashLineEditor(new RepoPathGuard(_Root), new HashSet<string> { "src/Other.cs" });
        var result = editor.ApplyRange("src/Foo.cs", 1, 1, "x", H("alpha"));
        Assert.False(result.Success);
        Assert.Equal("access denied: src/Foo.cs is outside the writable set", result.Error);
    }

    [Fact]
    public void ApplyRange_invalid_range()
    {
        var result = Editor().ApplyRange("src/Foo.cs", 0, 99, "x", "whatever");
        Assert.False(result.Success);
        Assert.Equal("invalid line range 0-99", result.Error);
    }

    [Fact]
    public void WriteAllLines_reverts_byte_identically_with_cached_endings()
    {
        File.WriteAllText(FilePath, "one\r\ntwo\nthree\r\nno-newline");
        var editor = Editor();
        var snapshot = File.ReadAllBytes(FilePath);

        // A read caches the raw per-line endings; a later revert restores them exactly,
        // even for the mixed-ending file editing itself would refuse.
        editor.ReadFileWithHashes("src/Foo.cs");
        File.WriteAllText(FilePath, "clobbered\ncontent\n");
        Assert.NotEqual(snapshot, File.ReadAllBytes(FilePath));

        editor.WriteAllLines("src/Foo.cs", ["one", "two", "three", "no-newline"]);
        Assert.Equal(snapshot, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void WriteAllLines_without_cache_uses_dominant_ending()
    {
        var editor = Editor();
        editor.WriteAllLines("src/Foo.cs", ["x", "y"]);
        Assert.Equal("x\ny\n", File.ReadAllText(FilePath));
    }

    [Fact]
    public void WriteAllLines_outside_writable_set_throws()
    {
        var editor = new HashLineEditor(new RepoPathGuard(_Root), new HashSet<string> { "src/Other.cs" });
        Assert.Throws<InvalidOperationException>(() => editor.WriteAllLines("src/Foo.cs", ["x"]));
    }

    [Fact]
    public void WriteAllLines_with_cached_endings_but_different_length_uses_dominant()
    {
        File.WriteAllText(FilePath, "one\r\ntwo\r\nthree\r\n");
        var editor = Editor();
        editor.ReadFileWithHashes("src/Foo.cs");
        editor.WriteAllLines("src/Foo.cs", ["x", "y"]);
        Assert.Equal("x\r\ny\r\n", File.ReadAllText(FilePath));
    }

    [Fact]
    public void ReadAllLines_returns_normalized_content()
    {
        File.WriteAllText(FilePath, "a  \r\nb\n");
        Assert.Equal(["a", "b"], Editor().ReadAllLines("src/Foo.cs"));
    }

    [Fact]
    public void ReadAllLines_denied_path_throws()
        => Assert.Throws<InvalidOperationException>(() => Editor().ReadAllLines("../evil"));

    [Fact]
    public void ReadAllLines_missing_throws()
        => Assert.Throws<FileNotFoundException>(() => Editor().ReadAllLines("src/ghost.cs"));

    [Fact]
    public void Smoke_10k_lines_100_edits()
    {
        var many = Enumerable.Range(1, 10_000).Select(i => $"line {i}").ToArray();
        File.WriteAllLines(FilePath, many);
        var editor = Editor();
        var edits = Enumerable.Range(0, 100)
            .Select(i => new LineEdit(H($"line {(i + 1) * 100}"), null, null, null, $"replaced {i}"))
            .ToArray();
        var result = editor.EditFile("src/Foo.cs", edits);
        Assert.Contains("applied 100 edits", result);
        Assert.Contains("replaced 50", Current());
    }
}
