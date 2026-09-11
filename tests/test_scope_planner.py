from reviewforge.git.chunker import _analysis_files, build_scopes, scope_document


DIFF = (
    "diff --git a/a.py b/a.py\n@@ -1 +1 @@\n-old\n+new\n"
    "diff --git a/b.py b/b.py\n@@ -1 +1 @@\n-old\n+new\n"
    "diff --git a/c.py b/c.py\n@@ -1 +1 @@\n-old\n+new\n"
)


def test_build_scopes_is_deterministic_and_respects_byte_cap():
    first, truncated = build_scopes(DIFF, ["a.py", "b.py", "c.py"], 70)
    second, second_truncated = build_scopes(DIFF, ["a.py", "b.py", "c.py"], 70)

    assert [(scope.scope_id, scope.files_text, scope.diff_text) for scope in first] == [
        (scope.scope_id, scope.files_text, scope.diff_text) for scope in second
    ]
    assert truncated is second_truncated
    assert all(len(scope.diff_text.encode()) <= 70 for scope in first)
    assert {path for scope in first for path in scope.files_text.splitlines()} == {
        "a.py",
        "b.py",
        "c.py",
    }


def test_crg_metadata_is_preserved_in_scope_artifact():
    scopes, _ = build_scopes(
        DIFF,
        ["a.py", "b.py", "c.py"],
        10_000,
        crg_analysis={
            "status": "ok",
            "impacted_files": ["b.py", "related.py"],
            "affected_flows": ["request -> handler"],
        },
        graph_context={"architecture": {"bridges_touched": [{"file_path": "bridge.py"}]}},
    )

    document = scope_document(scopes, crg_used=True, max_bytes=10_000)
    assert document["crg_used"] is True
    assert document["scopes"][0]["affected_flows"] == ["request -> handler"]
    assert document["scopes"][0]["context_files"] == ["related.py"]
    assert document["scopes"][0]["bridge_files"] == ["bridge.py"]



def test_crg_file_records_order_scopes_by_file_path():
    scopes, truncated = build_scopes(
        DIFF,
        ["a.py", "b.py", "c.py"],
        70,
        crg_analysis={"impacted_files": [{"file_path": "c.py"}, {"file": "b.py"}]},
    )

    assert truncated is False
    assert scopes[0].files_text.splitlines()[0] in {"b.py", "c.py"}


def test_crg_related_file_records_use_supported_path_fields():
    assert _analysis_files({"files": [{"file": "related.py"}]}, "files") == ["related.py"]

def test_malformed_or_missing_crg_uses_file_scopes():
    scopes, truncated = build_scopes(DIFF, ["a.py", "b.py", "c.py"], 10_000, crg_analysis={"bad": object()})

    assert truncated is False
    assert len(scopes) == 1
    assert scopes[0].files_text.splitlines() == ["a.py", "b.py", "c.py"]
    assert scopes[0].context_files == ()



def test_unparsed_diff_gets_a_bounded_scope():
    small, small_truncated = build_scopes("not a unified diff", ["a.py"], 100)
    large, large_truncated = build_scopes("x" * 200, ["a.py"], 20)

    assert small_truncated is False
    assert small[0].files_text == "a.py\n"
    assert large_truncated is True
    assert large[0].truncated is True
    assert len(large[0].diff_text.encode()) > 20


def test_oversized_file_is_truncated_without_dropping_other_files():
    oversized = "diff --git a/a.py b/a.py\n" + ("+x\n" * 100)
    normal = "diff --git a/b.py b/b.py\n@@ -1 +1 @@\n-old\n+new\n"
    scopes, truncated = build_scopes(oversized + normal, ["a.py", "b.py"], 80)

    assert truncated is True
    assert scopes[0].truncated is True
    assert any("b.py" in scope.files_text for scope in scopes)


def test_single_file_diff_uses_one_scope_without_chunking():
    scopes, truncated = build_scopes(
        "diff --git a/a.py b/a.py\n@@ -1 +1 @@\n-old\n+new\n",
        ["a.py"],
        1_000,
    )

    assert truncated is False
    assert len(scopes) == 1