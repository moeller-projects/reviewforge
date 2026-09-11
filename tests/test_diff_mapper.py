"""Regression tests for unified-diff path parsing and ADO anchors."""
from reviewforge.ado.diff_mapper import DiffLineMapper, collect_changed_files


QUOTED_PATH_DIFF = '''\
diff --git "a/dir with space/file.py" "b/dir with space/file.py"
--- "a/dir with space/file.py"
+++ "b/dir with space/file.py"
@@ -1 +1 @@
-old_value
+new_value
'''


def test_quoted_new_file_path_maps_inline_finding():
    mapper = DiffLineMapper.from_text(QUOTED_PATH_DIFF)

    context = mapper.find("dir with space/file.py", 1)

    assert context is not None
    assert context.file_path == "/dir with space/file.py"
    assert context.right_file_start == 1
    assert context.right_file_end == 1
    assert context.position == 1


def test_dev_null_headers_keep_create_and_delete_behavior():
    created_diff = """\
diff --git a/new.py b/new.py
new file mode 100644
--- /dev/null
+++ b/new.py
@@ -0,0 +1 @@
+created
"""
    deleted_diff = """\
diff --git a/removed.py b/removed.py
deleted file mode 100644
--- a/removed.py
+++ /dev/null
@@ -1 +0,0 @@
-removed
"""

    assert collect_changed_files(created_diff) == ["new.py"]
    assert collect_changed_files(deleted_diff) == []
