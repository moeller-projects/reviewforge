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

    def test_mixed_set_of_threads(self):
        # Multiple threads, mix of stale and current.
        threads = [
            _bot_thread(1, "/src/app.py", 4, "aaaaaaaaaaaa"),   # current
            _bot_thread(2, "/src/app.py", 99, "bbbbbbbbbbbb"),  # stale (line)
            _bot_thread(3, "/src/old.py", 1, "cccccccccccc"),   # stale (file)
            {"id": 4, "threadContext": None, "comments": [
                {"content": "Body\n<!-- prb:dddddddddddd -->\n"}
            ]},                                                  # general → skip
            {"id": 5, "threadContext": {"filePath": "/x.py"},
             "comments": [{"content": "no marker"}]},            # human → skip
        ]
        stale = posting.find_stale_bot_threads(
            threads,
            {"aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd"},
            {"src/app.py": {1, 2, 3, 4, 5}, "src/other.py": {1, 2}},
        )
        ids = sorted(e["threadId"] for e in stale)
        assert ids == [2, 3]


# ---------------------------------------------------------------------------
# stale_comment_body
# ---------------------------------------------------------------------------


class TestStaleCommentBody:
    def test_includes_short_sha_when_given(self):
        body = posting.stale_comment_body(short_sha="abcdef12")
        assert "abcdef12" in body
        assert "stale" in body.lower()
        assert "🤖" in body

    def test_falls_back_to_current_head(self):
        body = posting.stale_comment_body(short_sha="")
        assert "current HEAD" in body

    def test_none_sha_uses_current_head(self):
        body = posting.stale_comment_body(short_sha=None)
        assert "current HEAD" in body


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