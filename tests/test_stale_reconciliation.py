"""Tests for the stale-comment reconciliation feature.

When the source branch of a PR moves on, a bot finding posted in a
prior run may no longer anchor to a line that exists in the current
diff. The reconciliation pass (``posting.find_stale_bot_threads``)
identifies such threads so the post stage can append a "stale"
comment and the reviewer is not misled by an outdated inline finding.
"""
from __future__ import annotations

from unittest.mock import MagicMock, patch

import pytest

from reviewforge.ado import cli, diff_mapper, posting


DIFF_V1 = (
    "diff --git a/src/app.py b/src/app.py\n"
    "--- a/src/app.py\n"
    "+++ b/src/app.py\n"
    "@@ -1,3 +1,5 @@\n"
    " line1\n"
    "+added1\n"
    "+added2\n"
    " line2\n"
    " line3\n"
)

DIFF_V2_LINE_REMOVED = (
    "diff --git a/src/app.py b/src/app.py\n"
    "--- a/src/app.py\n"
    "+++ b/src/app.py\n"
    "@@ -1,5 +1,2 @@\n"
    " line1\n"
    " line2\n"
    " line3\n"
)

DIFF_FILE_GONE = (
    "diff --git a/src/other.py b/src/other.py\n"
    "--- a/src/other.py\n"
    "+++ b/src/other.py\n"
    "@@ -1,1 +1,2 @@\n"
    " x\n"
    "+y\n"
)


# ---------------------------------------------------------------------------
# DiffLineMapper.line_set
# ---------------------------------------------------------------------------


class TestLineSet:
    def test_returns_all_lines_in_hunks(self):
        m = diff_mapper.DiffLineMapper.from_text(DIFF_V1)
        # Hunk 1: new-file lines 1..5 (1, 2 context+added, 3 added, 4 context, 5 context)
        assert m.line_set("src/app.py") == {1, 2, 3, 4, 5}

    def test_leading_slash_tolerated(self):
        m = diff_mapper.DiffLineMapper.from_text(DIFF_V1)
        assert m.line_set("/src/app.py") == {1, 2, 3, 4, 5}

    def test_missing_file_returns_empty(self):
        m = diff_mapper.DiffLineMapper.from_text(DIFF_V1)
        assert m.line_set("nope.py") == set()

    def test_empty_diff_returns_empty(self):
        m = diff_mapper.DiffLineMapper.from_text("")
        assert m.line_set("x.py") == set()

    def test_helper_function(self):
        anchors = diff_mapper.line_set_for_file(DIFF_V1, "src/app.py")
        assert anchors == {1, 2, 3, 4, 5}


# ---------------------------------------------------------------------------
# find_stale_bot_threads — pure logic, no HTTP
# ---------------------------------------------------------------------------


def _bot_thread(thread_id, file_path, line, marker):
    return {
        "id": thread_id,
        "threadContext": (
            {"filePath": file_path, "rightFileStart": {"line": line, "offset": 1}}
            if file_path is not None
            else None
        ),
        "comments": [{"content": f"Body\n<!-- prb:{marker} -->\n"}],
    }


class TestFindStaleBotThreads:
    def test_returns_empty_when_no_bot_threads(self):
        # Threads exist but carry no bot marker.
        threads = [_bot_thread(1, "/src/app.py", 4, "irrelevant")]
        # Empty marker set → thread is treated as non-bot.
        assert posting.find_stale_bot_threads(threads, set(), {"src/app.py": {1, 2, 3, 4, 5}}) == []

    def test_line_no_longer_in_diff_is_stale(self):
        # Prior anchor on line 6 of src/app.py. Current diff only covers 1..5.
        threads = [_bot_thread(1, "/src/app.py", 6, "abc123def456")]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        )
        assert len(stale) == 1
        assert stale[0]["threadId"] == 1
        assert stale[0]["reason"] == "line_no_longer_in_diff"
        assert stale[0]["file"] == "src/app.py"
        assert stale[0]["line"] == 6

    def test_line_still_in_diff_is_current(self):
        # Prior anchor on line 4 of src/app.py. Current diff covers 1..5.
        threads = [_bot_thread(1, "/src/app.py", 4, "abc123def456")]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        )
        assert stale == []

    def test_file_no_longer_in_diff_is_stale(self):
        # Prior anchor on src/app.py:42, but current diff has only src/other.py.
        threads = [_bot_thread(1, "/src/app.py", 42, "abc123def456")]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/other.py": {1, 2}},
        )
        assert len(stale) == 1
        assert stale[0]["reason"] == "file_no_longer_in_diff"

    @pytest.mark.parametrize("status", ["fixed", "wontFix", "wontfix", "closed"])
    def test_terminal_thread_status_is_not_stale(self, status):
        thread = _bot_thread(1, "/src/app.py", 6, "abc123def456")
        thread["status"] = status
        stale = posting.find_stale_bot_threads(
            [thread],
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        )
        assert stale == []

    def test_deleted_thread_is_not_stale(self):
        thread = _bot_thread(1, "/src/app.py", 6, "abc123def456")
        thread["isDeleted"] = True
        assert posting.find_stale_bot_threads(
            [thread],
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        ) == []


    def test_general_comment_never_stale(self):
        # Work-item findings are posted as general PR comments (no threadContext).
        threads = [{"id": 1, "threadContext": None, "comments": [
            {"content": "Body\n<!-- prb:abc123def456 -->\n"}
        ]}]
        stale = posting.find_stale_bot_threads(threads, {"abc123def456"}, {})
        assert stale == []

    def test_file_level_anchor_never_stale(self):
        # File-level anchor: threadContext present but no line number.
        threads = [{
            "id": 1,
            "threadContext": {"filePath": "/src/app.py"},
            "comments": [{"content": "Body\n<!-- prb:abc123def456 -->\n"}],
        }]
        stale = posting.find_stale_bot_threads(threads, {"abc123def456"}, {})
        assert stale == []

    def test_human_thread_skipped(self):
        # No marker in comment body → human thread, never stale.
        threads = [{
            "id": 1,
            "threadContext": {"filePath": "/x.py", "rightFileStart": {"line": 99}},
            "comments": [{"content": "Just a comment, no marker."}],
        }]
        stale = posting.find_stale_bot_threads(threads, {"abc123def456"}, {"x.py": {99}})
        assert stale == []

    def test_just_posted_threads_skipped(self):
        # A thread created in this run obviously matches the current diff.
        threads = [_bot_thread(1, "/src/app.py", 6, "abc123def456")]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
            just_posted_thread_ids={1},
        )
        assert stale == []

    def test_already_annotated_thread_not_stale_again(self):
        # A stale thread that already carries a prb-stale follow-up (from a
        # prior run) must not be re-flagged, so a re-run is idempotent.
        threads = [{
            "id": 1,
            "threadContext": {"filePath": "/src/app.py",
                              "rightFileStart": {"line": 6, "offset": 1}},
            "comments": [
                {"content": "Body\n<!-- prb:abc123def456 -->\n"},
                {"content": "stale note\nprb-stale:abc123def456\n"},
            ],
        }]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        )
        assert stale == []

    def test_annotated_thread_with_other_key_not_skipped(self):
        # The stale-marker idempotency is per-thread, not global: a stale
        # marker with a *different* key must not suppress this thread.
        threads = [{
            "id": 1,
            "threadContext": {"filePath": "/src/app.py",
                              "rightFileStart": {"line": 6, "offset": 1}},
            "comments": [
                {"content": "Body\n<!-- prb:abc123def456 -->\n"},
                {"content": "stale note\nprb-stale:ffffffffffff\n"},
            ],
        }]
        stale = posting.find_stale_bot_threads(
            threads,
            {"abc123def456"},
            {"src/app.py": {1, 2, 3, 4, 5}},
        )
        assert len(stale) == 1
        assert stale[0]["threadId"] == 1
        assert stale[0]["key"] == "abc123def456"


# ---------------------------------------------------------------------------
# stale_comment_body
# ---------------------------------------------------------------------------


    def test_append_stale_marker_when_key_given(self):
        body = posting.stale_comment_body(short_sha="abcdef12", key="abc123def456")
        assert "stale anchor" in body
        assert "re-evaluate it against the new code" in body
        assert f"\n{posting.stale_marker('abc123def456')}" in body
        # The marker sits on its own final line.
        assert body.rstrip().endswith(posting.stale_marker("abc123def456"))

    def test_no_stale_marker_without_key(self):
        body = posting.stale_comment_body(short_sha="abcdef12")
        assert posting.stale_marker("abc123def456") not in body


class TestStaleMarkerHelpers:
    def test_stale_marker_grammar_is_distinct(self):
        marker = posting.stale_marker("abc123def456")
        assert marker == "prb-stale:abc123def456"
        # It must NOT match the finding-dedupe regex, so a stale note is
        # never mistaken for (or collected as) a finding marker.
        assert posting._MARKER_RE.search(marker) is None  # noqa: SLF001

    def test_existing_bot_markers_ignores_stale_notes(self):
        # A thread with only a stale follow-up (plus original) yields only the
        # original finding key from existing_bot_markers.
        thread = {
            "id": 1,
            "comments": [
                {"content": "Body\n<!-- prb:abc123def456 -->\n"},
                {"content": "stale note\nprb-stale:abc123def456\n"},
            ],
        }
        assert posting.existing_bot_markers([thread]) == {"abc123def456"}


# ---------------------------------------------------------------------------
# AdoClient.add_comment (unit)
# ---------------------------------------------------------------------------


class TestAdoClientAddComment:
    def test_add_comment_posts_to_thread_comments(self):
        from reviewforge.ado.client import AdoClient
        c = AdoClient(org="https://dev.azure.com/x", project="p", repo="r", token="t")
        # Patch the underlying _request so we don't hit the network.
        c._request = MagicMock(return_value={"id": 99})  # noqa: SLF001
        out = c.add_comment(pr_id=42, thread_id=7, content="hi")
        c._request.assert_called_once()  # noqa: SLF001
        call_args = c._request.call_args  # noqa: SLF001
        assert call_args.args[0] == "POST"
        assert call_args.args[1].endswith("/threads/7/comments")
        body = call_args.args[2]
        assert body == {"content": "hi", "commentType": "text"}
        assert out == {"id": 99}


# ---------------------------------------------------------------------------
# End-to-end: command_post_findings runs the stale pass and records it.
# ---------------------------------------------------------------------------


def _findings_file(tmp_path, findings):
    p = tmp_path / "findings.json"
    p.write_text('{"summary": "", "findings": ' + str(findings).replace("'", '"') + "}")
    return p


def _diff_file(tmp_path, text):
    p = tmp_path / "diff.patch"
    p.write_text(text)
    return tmp_path


def _args(findings_path, out_path):
    from types import SimpleNamespace
    return SimpleNamespace(
        org="contoso",
        project="Pay",
        repo="api",
        pr=1,
        findings=str(findings_path),
        out=str(out_path),
    )


class TestCommandPostFindingsStalePass:
    def test_stale_thread_gets_annotated(self, tmp_path, monkeypatch):
        # Findings: empty — nothing new to post. We'll just exercise the
        # stale pass against a fake existing thread.
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        # Diff covers src/app.py lines 1..5 only.
        _diff_file(tmp_path, DIFF_V1)

        # Existing bot thread anchored at line 6 → stale.
        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {
            "reviewers": [],
            "lastMergeCommit": {"commitId": "1234567890abcdef"},
        }
        client.get_threads.return_value = [bot_thread]
        client.create_thread.return_value = {"id": 999}
        client.add_comment.return_value = {"id": 1000}

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)
        monkeypatch.delenv("ANNOTATE_STALE", raising=False)  # default-on

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.add_comment.assert_called_once()
        call_args = client.add_comment.call_args
        assert call_args.args[0] == 1  # pr_id
        assert call_args.args[1] == 7  # thread_id
        assert "12345678" in call_args.args[2]  # short sha
        assert "stale" in call_args.args[2].lower()

        result = __import__("json").loads(out_file.read_text())
        assert result["annotated_stale"] == 1
        assert result["stale_thread_ids"] == [7]

    def test_current_thread_not_annotated(self, tmp_path, monkeypatch):
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V1)

        # Bot thread anchored at line 4 — still in the diff.
        bot_thread = _bot_thread(7, "/src/app.py", 4, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.add_comment.assert_not_called()
        result = __import__("json").loads(out_file.read_text())
        assert "annotated_stale" not in result or result.get("annotated_stale", 0) == 0

    def test_annotate_stale_disabled(self, tmp_path, monkeypatch):
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V1)

        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("ANNOTATE_STALE", "0")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.add_comment.assert_not_called()

    def test_missing_diff_skips_stale_reconciliation(self, tmp_path, monkeypatch):
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"

        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("ANNOTATE_STALE", raising=False)
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.add_comment.assert_not_called()

    def test_just_posted_thread_not_flagged_stale(self, tmp_path, monkeypatch):
        # A finding posted in this run on line 6 would be marked stale
        # by the rule if we did not exclude just-posted threads.
        # Without the exclusion, the bot would annotate its own brand
        # new comment. Verify it does not.
        finding = {
            "severity": "major",
            "title": "T",
            "message": "M",
            "file": "src/app.py",
            "line": 6,
        }
        findings_file = _findings_file(tmp_path, [finding])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V2_LINE_REMOVED)  # line 6 is gone in this diff

        # The thread for the just-posted finding gets returned by the
        # next get_threads call (the post stage has already added it
        # to result["comments"], so the stale pass must skip it).
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = []  # empty on first fetch
        client.create_thread.return_value = {"id": 42}

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.create_thread.assert_called_once()
        # Critical: the bot did not annotate its own brand-new thread.
        client.add_comment.assert_not_called()

    def test_add_comment_failure_does_not_break_run(self, tmp_path, monkeypatch):
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V1)

        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]
        client.add_comment.side_effect = RuntimeError("network down")

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        # The run still succeeds; stale annotation is best-effort.
        assert rc == 0
        client.add_comment.assert_called_once()

    def test_failed_stale_write_not_counted(self, tmp_path, monkeypatch):
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V1)

        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]
        client.add_comment.side_effect = RuntimeError("network down")

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        result = __import__("json").loads(out_file.read_text())
        # A failed write is not reported as a successful annotation.
        assert result.get("annotated_stale", 0) == 0
        assert result.get("stale_thread_ids", []) == []

    def test_rerun_does_not_duplicate_stale_comment(self, tmp_path, monkeypatch):
        # A thread that already carries a stale follow-up (from a prior run)
        # must not be annotated again on a re-run.
        findings_file = _findings_file(tmp_path, [])
        out_file = tmp_path / "out.json"
        _diff_file(tmp_path, DIFF_V1)

        bot_thread = _bot_thread(7, "/src/app.py", 6, "abc123def456")
        # The thread already contains the stale follow-up from a prior run.
        bot_thread["comments"].append(
            {"content": "stale note\nprb-stale:abc123def456\n"}
        )
        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [bot_thread]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = cli.command_post_findings(_args(findings_file, out_file))
        assert rc == 0
        client.add_comment.assert_not_called()
        result = __import__("json").loads(out_file.read_text())
        assert result.get("annotated_stale", 0) == 0
        assert result.get("stale_thread_ids", []) == []