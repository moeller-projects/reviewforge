"""Unit tests for ``reviewforge.ado.posting`` helpers.

These tests cover the work-item finding detection and transformation
helpers added as defense in depth: even if the review prompt says
``file: null, line: null`` for work item findings, the posting path
must strip a guessed file/line before posting so the finding is
published as a general PR comment, not inline.
"""

from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

import pytest

from reviewforge.ado import posting


# ---------------------------------------------------------------------------
# is_work_item_finding
# ---------------------------------------------------------------------------


class TestIsWorkItemFinding:
    def test_canonical_prefix(self):
        assert posting.is_work_item_finding(
            {"title": "Work item #1234 requirement not addressed: cache invalidation"}
        )

    def test_prefix_with_id_only(self):
        assert posting.is_work_item_finding({"title": "Work item #1 missing"})

    def test_case_insensitive(self):
        assert posting.is_work_item_finding(
            {"title": "work item #42 requirement not addressed: foo"}
        )

    def test_leading_whitespace_tolerated(self):
        assert posting.is_work_item_finding(
            {"title": "  Work item #99: bar"}
        )

    def test_non_work_item_title(self):
        assert not posting.is_work_item_finding(
            {"title": "Possible token leak in logger"}
        )

    def test_missing_title(self):
        assert not posting.is_work_item_finding({})
        assert not posting.is_work_item_finding({"title": None})
        assert not posting.is_work_item_finding({"title": ""})

    def test_similar_but_not_prefix(self):
        # "See work item #5" is not a work item finding — it's a
        # comment about one. The strict prefix avoids catching these.
        assert not posting.is_work_item_finding(
            {"title": "See work item #5 for the original report"}
        )

    def test_no_hash(self):
        # "Work item foo" without a numeric id should not match.
        assert not posting.is_work_item_finding(
            {"title": "Work item foo requirement"}
        )


# ---------------------------------------------------------------------------
# as_general_comment
# ---------------------------------------------------------------------------


class TestAsGeneralComment:
    def test_strips_file_and_line(self):
        out = posting.as_general_comment(
            {
                "file": "src/payments/charge.ts",
                "line": 87,
                "severity": "blocker",
                "title": "Work item #42 requirement not addressed",
                "message": "Acceptance criterion X missing.",
            }
        )
        assert out["file"] is None
        assert out["line"] is None
        # Other fields preserved.
        assert out["severity"] == "blocker"
        assert out["title"] == "Work item #42 requirement not addressed"
        assert out["message"] == "Acceptance criterion X missing."

    def test_does_not_mutate_input(self):
        original = {
            "file": "src/x.ts",
            "line": 10,
            "severity": "major",
            "title": "Work item #1 missing",
        }
        snapshot = dict(original)
        posting.as_general_comment(original)
        assert original == snapshot

    def test_already_null_passes_through(self):
        out = posting.as_general_comment(
            {
                "file": None,
                "line": None,
                "severity": "major",
                "title": "Work item #1 missing",
            }
        )
        assert out["file"] is None
        assert out["line"] is None


# ---------------------------------------------------------------------------
# CommandPostFindings integration — work item findings
# ---------------------------------------------------------------------------
#
# The PostToAdoStage and the legacy CLI both delegate to
# ``command_post_findings`` via the ``call_helper`` mechanism. So the
# integration test for the new behavior lives next to that function.
# Imported here to keep the helper tests grouped with the integration
# tests that exercise them end-to-end.


def _args(findings_path: Path, out_path: Path) -> SimpleNamespace:
    return SimpleNamespace(
        org="contoso",
        project="Payments",
        repo="api",
        pr=42,
        findings=str(findings_path),
        out=str(out_path),
    )


def _write_findings(tmp_path: Path, findings: list) -> tuple[Path, Path]:
    findings_file = tmp_path / "findings.json"
    out_file = tmp_path / "result.json"
    findings_file.write_text(json.dumps({"summary": "", "findings": findings}))
    return findings_file, out_file


def _mock_client() -> MagicMock:
    client = MagicMock()
    client.get_pr.return_value = {"reviewers": []}
    client.get_threads.return_value = []
    client.create_thread.return_value = {"id": 99}
    return client


class TestCommandPostFindingsWorkItem:
    def test_work_item_finding_with_guessed_file_posted_as_general_comment(
        self, tmp_path, monkeypatch
    ):
        # Model "helps" by guessing a file/line. Posting must strip it
        # and post as a general PR comment (no threadContext), not drop
        # the finding, and not post inline.
        findings_file, out_file = _write_findings(
            tmp_path,
            [
                {
                    "severity": "blocker",
                    "title": "Work item #42 requirement not addressed: cache invalidation",
                    "message": "Acceptance criterion X missing.",
                    "file": "src/payments/charge.ts",
                    "line": 87,
                }
            ],
        )

        mock_client = _mock_client()
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        from reviewforge.ado import cli as m

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(_args(findings_file, out_file))

        assert rc == 0
        # The finding was posted (not dropped).
        mock_client.create_thread.assert_called_once()
        # And it was posted without a threadContext — i.e. as a general
        # PR comment, not inline on the guessed line.
        call_args = mock_client.create_thread.call_args
        thread_body = call_args.args[1] if len(call_args.args) > 1 else call_args.kwargs.get("body")
        assert thread_body is not None
        assert "threadContext" not in thread_body
        result = json.loads(out_file.read_text())
        assert result["created"] == 1
        assert result["skipped"] == 0

    def test_work_item_finding_with_null_file_unchanged(
        self, tmp_path, monkeypatch
    ):
        # Work item finding that already follows the prompt's contract
        # (file: null, line: null) posts as general comment with no
        # threadContext — same as before, regression test.
        findings_file, out_file = _write_findings(
            tmp_path,
            [
                {
                    "severity": "major",
                    "title": "Work item #7 requirement not addressed: y",
                    "message": "Missing.",
                    "file": None,
                    "line": None,
                }
            ],
        )
        # A real pipeline run has diff.patch, so exercise the mapper-present
        # path where general findings previously became no_line_mapping skips.
        (tmp_path / "diff.patch").write_text(
            "diff --git a/src/changed.ts b/src/changed.ts\n"
            "--- a/src/changed.ts\n"
            "+++ b/src/changed.ts\n"
            "@@ -0,0 +1 @@\n"
            "+changed\n",
            encoding="utf-8",
        )

        mock_client = _mock_client()
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        from reviewforge.ado import cli as m

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(_args(findings_file, out_file))

        assert rc == 0
        mock_client.create_thread.assert_called_once()
        call_args = mock_client.create_thread.call_args
        thread_body = call_args.args[1] if len(call_args.args) > 1 else call_args.kwargs.get("body")
        assert "threadContext" not in thread_body

    def test_non_work_item_finding_with_file_keeps_inline_posting(
        self, tmp_path, monkeypatch
    ):
        # Regression test: a normal code finding (no work item prefix)
        # must STILL go through the file/line mapping path. The
        # work-item rule must not affect normal findings.
        findings_file, out_file = _write_findings(
            tmp_path,
            [
                {
                    "severity": "major",
                    "title": "Token in log",
                    "message": "Sensitive data leaked.",
                    "file": "src/log.ts",
                    "line": 10,
                }
            ],
        )

        mock_client = _mock_client()
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        from reviewforge.ado import cli as m

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(_args(findings_file, out_file))

        assert rc == 0
        mock_client.create_thread.assert_called_once()
        call_args = mock_client.create_thread.call_args
        thread_body = call_args.args[1] if len(call_args.args) > 1 else call_args.kwargs.get("body")
        # Code finding keeps its threadContext (inline posting).
        assert "threadContext" in thread_body

    def test_work_item_finding_with_unmappable_file_not_dropped(
        self, tmp_path, monkeypatch
    ):
        # Work item finding with a guessed file that the diff mapper
        # cannot map to a line. Pre-patch this would silently drop the
        # finding (no_line_mapping). Post-patch the file is stripped
        # up front, so the finding is posted as a general comment.
        findings_file, out_file = _write_findings(
            tmp_path,
            [
                {
                    "severity": "major",
                    "title": "Work item #99 requirement not addressed: z",
                    "message": "AC not met.",
                    "file": "src/does_not_exist.ts",
                    "line": 1,
                }
            ],
        )

        mock_client = _mock_client()
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        from reviewforge.ado import cli as m

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(_args(findings_file, out_file))

        assert rc == 0
        mock_client.create_thread.assert_called_once()
        result = json.loads(out_file.read_text())
        assert result["created"] == 1
        # No silent drop.
        assert result["skipped"] == 0
        assert result["skipped_reasons"]["no_line_mapping"] == 0

    def test_dedupe_key_stable_across_guessed_and_null_file(
        self, tmp_path, monkeypatch
    ):
        # A work item finding posted once with a guessed file and then
        # again with file: null must dedupe — otherwise a model that
        # varies the guess between reruns would re-post every time.
        # The fix strips the file up front, so the dedupe key is
        # computed against the general-comment form.

        # Pre-compute the dedupe key both forms will share. The posting
        # path strips file/line on the way in, so the canonical key is
        # the one computed against (file=None, line=None).
        from reviewforge.ado.posting import dedupe_key

        canonical = {
            "file": None,
            "line": None,
            "severity": "blocker",
            "title": "Work item #42 requirement not addressed: cache invalidation",
            "message": "AC missing.",
        }
        key = dedupe_key(canonical)
        existing_marker = f"prb:{key}"  # raw form, what the dedupe scanner detects

        # Second run: model follows the prompt (file: null). The
        # dedupe scanner sees the marker from the first run.
        findings_file_b, out_file_b = _write_findings(
            tmp_path,
            [
                {
                    "severity": "blocker",
                    "title": "Work item #42 requirement not addressed: cache invalidation",
                    "message": "AC missing.",
                    "file": None,
                    "line": None,
                }
            ],
        )

        client = MagicMock()
        client.get_pr.return_value = {"reviewers": []}
        client.get_threads.return_value = [
            {"comments": [{"content": f"prb comment\n{existing_marker}"}]}
        ]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        from reviewforge.ado import cli as m

        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = m.command_post_findings(_args(findings_file_b, out_file_b))
        assert rc == 0
        client.create_thread.assert_not_called()
        result = json.loads(out_file_b.read_text())
        assert result["created"] == 0
        assert result["skipped"] == 1
        assert result["skipped_reasons"]["duplicate"] == 1

        # And the same dedupe applies if the model had guessed a file
        # this time around (mirrors the first-run scenario).
        findings_file_a, out_file_a = _write_findings(
            tmp_path,
            [
                {
                    "severity": "blocker",
                    "title": "Work item #42 requirement not addressed: cache invalidation",
                    "message": "AC missing.",
                    "file": "src/payments/charge.ts",
                    "line": 87,
                }
            ],
        )
        with patch("reviewforge.ado.cli.AdoClient", return_value=client):
            rc = m.command_post_findings(_args(findings_file_a, out_file_a))
        assert rc == 0
        # The guessed-file form also dedupes against the same marker.
        client.create_thread.assert_not_called()
