"""pytest suite for the isolated ``reviewforge.ado.cli`` CLI.

These tests cover the public helper surface retained for external consumers.
"""

from __future__ import annotations

import hashlib
import json
import sys
import textwrap
from io import StringIO
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock, patch

import pytest

# Import the canonical implementation from the package.
from reviewforge.ado import cli as m  # noqa: E402  (ADO CLI surface)
from reviewforge.exceptions import AdoApiError  # noqa: E402


# ---------------------------------------------------------------------------
# normalize_org
# ---------------------------------------------------------------------------


class TestNormalizeOrg:
    def test_short_name_becomes_url(self):
        url, short = m.normalize_org("contoso")
        assert url == "https://dev.azure.com/contoso"
        assert short == "contoso"

    def test_dev_azure_url_round_trips(self):
        url, short = m.normalize_org("https://dev.azure.com/contoso")
        assert url == "https://dev.azure.com/contoso"
        assert short == "contoso"

    def test_dev_azure_url_with_trailing_slash(self):
        url, short = m.normalize_org("https://dev.azure.com/contoso/")
        assert "contoso" in url
        assert short == "contoso"

    def test_visualstudio_url(self):
        url, short = m.normalize_org("https://contoso.visualstudio.com")
        assert url == "https://contoso.visualstudio.com"
        assert short == "contoso"

    def test_unknown_url_raises(self):
        with pytest.raises(AdoApiError):
            m.normalize_org("https://example.com/foo")


# ---------------------------------------------------------------------------
# truncate
# ---------------------------------------------------------------------------


class TestTruncate:
    def test_short_text_unchanged(self):
        assert m.truncate("hello", 100) == "hello"

    def test_long_text_truncated(self):
        text = "x" * 500
        result = m.truncate(text, 200)
        assert len(result) <= 200
        assert "truncated" in result

    def test_none_coerced_to_empty(self):
        assert m.truncate(None, 100) == ""

    def test_exact_length_unchanged(self):
        text = "a" * 100
        assert m.truncate(text, 100) == text


# ---------------------------------------------------------------------------
# fence
# ---------------------------------------------------------------------------


class TestFence:
    def test_plain_code_uses_triple_backtick(self):
        result = m.fence("print('hi')")
        assert result.startswith("```")
        assert result.endswith("```")

    def test_code_with_triple_backtick_uses_quad(self):
        result = m.fence("``` some code ```")
        assert result.startswith("````")

    def test_output_contains_input(self):
        code = "def foo(): pass"
        result = m.fence(code)
        assert code in result


# ---------------------------------------------------------------------------
# worst_rank / should_threshold
# ---------------------------------------------------------------------------


class TestWorstRank:
    def test_empty_findings(self):
        assert m.worst_rank([]) == 0

    def test_single_blocker(self):
        assert m.worst_rank([{"severity": "blocker"}]) == 4

    def test_mixed_severities(self):
        findings = [{"severity": "nit"}, {"severity": "major"}, {"severity": "minor"}]
        assert m.worst_rank(findings) == 3  # major


class TestShouldThreshold:
    def test_none_threshold_always_false(self):
        findings = [{"severity": "blocker"}]
        assert m.should_threshold(findings, "none") is False

    def test_major_threshold_with_blocker(self):
        assert m.should_threshold([{"severity": "blocker"}], "major") is True

    def test_major_threshold_with_minor(self):
        assert m.should_threshold([{"severity": "minor"}], "major") is False

    def test_empty_findings_always_false(self):
        assert m.should_threshold([], "nit") is False


# ---------------------------------------------------------------------------
# key_of
# ---------------------------------------------------------------------------


class TestKeyOf:
    def test_deterministic(self):
        f = {"file": "src/foo.ts", "line": 5, "severity": "major", "title": "Test"}
        assert m.key_of(f) == m.key_of(f)

    def test_different_fields_produce_different_keys(self):
        f1 = {"file": "a.ts", "line": 1, "severity": "major", "title": "A"}
        f2 = {"file": "b.ts", "line": 1, "severity": "major", "title": "A"}
        assert m.key_of(f1) != m.key_of(f2)

    def test_key_is_12_hex_chars(self):
        f = {"file": None, "line": None, "severity": "nit", "title": "Foo"}
        key = m.key_of(f)
        assert len(key) == 12
        assert all(c in "0123456789abcdef" for c in key)


# ---------------------------------------------------------------------------
# comment_body
# ---------------------------------------------------------------------------


class TestCommentBody:
    _base = {
        "severity": "major",
        "title": "Credential Leak",
        "message": "Token exposed in log.",
        "confidence": "high",
        "suggestion": None,
        "file": "src/logger.ts",
        "line": 42,
    }

    def test_contains_severity_label(self):
        body = m.comment_body(self._base, "abc123", 20000)
        assert "🟠 major" in body

    def test_contains_title(self):
        body = m.comment_body(self._base, "abc123", 20000)
        assert "Credential Leak" in body

    def test_contains_marker(self):
        key = "deadbeef1234"
        body = m.comment_body(self._base, key, 20000)
        assert f"prb:{key}" in body

    def test_suggestion_fenced(self):
        f = {**self._base, "suggestion": "remove the log line"}
        body = m.comment_body(f, "key1", 20000)
        assert "```" in body
        assert "remove the log line" in body

    def test_evidence_included(self):
        f = {
            **self._base,
            "evidence": {
                "whyNewInThisPr": "introduced in this commit",
                "whyNotIntentional": "looks accidental",
                "contextFilesRead": ["src/app.ts"],
                "changedLines": [10, 11],
            },
        }
        body = m.comment_body(f, "key2", 20000)
        assert "introduced in this commit" in body
        assert "looks accidental" in body
        assert "src/app.ts" in body

    def test_respects_max_chars(self):
        body = m.comment_body(self._base, "key3", 100)
        assert len(body) <= 100


# ---------------------------------------------------------------------------
# simplify_thread
# ---------------------------------------------------------------------------


class TestSimplifyThread:
    def test_basic_fields(self):
        thread = {
            "id": 7,
            "status": "active",
            "threadContext": {"filePath": "/src/foo.ts", "rightFileStart": {"line": 10}},
            "comments": [{"content": "Nice catch", "author": {"displayName": "Alice"}}],
        }
        result = m.simplify_thread(thread)
        assert result["id"] == 7
        assert result["status"] == "active"
        assert result["filePath"] == "/src/foo.ts"
        assert result["line"] == 10
        assert result["firstComment"] == "Nice catch"
        assert result["author"] == "Alice"

    def test_empty_thread(self):
        result = m.simplify_thread({})
        assert result["id"] is None
        assert result["author"] == "unknown"
        assert result["firstComment"] == ""


# ---------------------------------------------------------------------------
# extract_json
# ---------------------------------------------------------------------------


class TestExtractJson:
    def test_plain_json(self, tmp_path):
        p = tmp_path / "f.json"
        p.write_text('{"a": 1}')
        assert m.extract_json(p) == {"a": 1}

    def test_markdown_fenced_json(self, tmp_path):
        p = tmp_path / "f.json"
        p.write_text("```json\n{\"b\": 2}\n```")
        assert m.extract_json(p) == {"b": 2}

    def test_invalid_json_raises(self, tmp_path):
        p = tmp_path / "f.json"
        p.write_text("not json at all")
        with pytest.raises(json.JSONDecodeError):
            m.extract_json(p)


# ---------------------------------------------------------------------------
# validate_findings
# ---------------------------------------------------------------------------


class TestValidateFindings:
    def _doc(self, **overrides):
        base = {
            "summary": "All good",
            "findings": [],
        }
        base.update(overrides)
        return base

    def test_valid_empty(self):
        summary, findings = m.validate_findings(self._doc())
        assert summary == "All good"
        assert findings == []

    def test_valid_finding(self):
        doc = self._doc(
            findings=[
                {
                    "severity": "major",
                    "title": "Foo",
                    "message": "Bar",
                    "file": "src/x.ts",
                    "line": 5,
                }
            ]
        )
        _, findings = m.validate_findings(doc)
        assert len(findings) == 1
        assert findings[0]["severity"] == "major"
        assert findings[0]["file"] == "src/x.ts"

    def test_file_leading_slash_stripped(self):
        doc = self._doc(
            findings=[{"severity": "nit", "title": "T", "message": "M", "file": "/src/y.ts"}]
        )
        _, findings = m.validate_findings(doc)
        assert not findings[0]["file"].startswith("/")

    def test_invalid_severity_raises(self):
        doc = self._doc(findings=[{"severity": "critical", "title": "T", "message": "M"}])
        with pytest.raises(SystemExit):
            m.validate_findings(doc)

    def test_missing_message_raises(self):
        doc = self._doc(findings=[{"severity": "major", "title": "T", "message": ""}])
        with pytest.raises(SystemExit):
            m.validate_findings(doc)

    def test_not_a_dict_raises(self):
        with pytest.raises(SystemExit):
            m.validate_findings([])

    def test_summary_not_string_raises(self):
        with pytest.raises(SystemExit):
            m.validate_findings({"summary": 123, "findings": []})

    def test_invalid_confidence_raises(self):
        doc = self._doc(
            findings=[
                {"severity": "major", "title": "T", "message": "M", "confidence": "unknown"}
            ]
        )
        with pytest.raises(SystemExit):
            m.validate_findings(doc)

    def test_valid_confidence_values(self):
        for conf in ("high", "medium", "low"):
            doc = self._doc(
                findings=[
                    {"severity": "nit", "title": "T", "message": "M", "confidence": conf}
                ]
            )
            _, findings = m.validate_findings(doc)
            assert findings[0]["confidence"] == conf

    def test_evidence_normalized(self):
        doc = self._doc(
            findings=[
                {
                    "severity": "blocker",
                    "title": "T",
                    "message": "M",
                    "evidence": {
                        "changed_lines": [1, 2],
                        "context_files_read": ["a.ts"],
                        "why_new_in_this_pr": "new",
                        "why_not_intentional": "accident",
                    },
                }
            ]
        )
        _, findings = m.validate_findings(doc)
        ev = findings[0]["evidence"]
        assert ev["changedLines"] == [1, 2]
        assert ev["contextFilesRead"] == ["a.ts"]
        assert ev["whyNewInThisPr"] == "new"
        assert ev["whyNotIntentional"] == "accident"


# ---------------------------------------------------------------------------
# current_reviewer_id
# ---------------------------------------------------------------------------


class TestCurrentReviewerId:
    def _client(self, user_id="u1", unique_name="bot@example.com"):
        client = MagicMock()
        client.connection_data.return_value = {
            "authenticatedUser": {"id": user_id, "uniqueName": unique_name}
        }
        return client

    def test_match_by_id(self):
        client = self._client(user_id="abc")
        pr = {"reviewers": [{"id": "abc", "uniqueName": "other@x.com"}]}
        assert m.current_reviewer_id(client, pr) == "abc"

    def test_match_by_unique_name(self):
        client = self._client(user_id="uid-1", unique_name="bot@example.com")
        pr = {"reviewers": [{"id": "uid-999", "uniqueName": "Bot@Example.Com"}]}
        assert m.current_reviewer_id(client, pr) == "uid-999"

    def test_no_match_returns_none(self):
        client = self._client(user_id="uid-1", unique_name="bot@example.com")
        pr = {"reviewers": [{"id": "uid-2", "uniqueName": "other@x.com"}]}
        assert m.current_reviewer_id(client, pr) is None

    def test_empty_reviewers_returns_none(self):
        client = self._client()
        assert m.current_reviewer_id(client, {"reviewers": []}) is None


# ---------------------------------------------------------------------------
# command_post_findings (integration with mocked ADO client)
# ---------------------------------------------------------------------------


class TestCommandPostFindings:
    def _args(self, findings_path, out_path):
        return SimpleNamespace(
            org="contoso",
            project="Payments",
            repo="api",
            pr=42,
            findings=str(findings_path),
            out=str(out_path),
        )

    def _findings_doc(self, findings=None, summary="Looks good"):
        return {"summary": summary, "findings": findings or []}

    def test_clean_run_no_vote(self, tmp_path, monkeypatch):
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        findings_file.write_text(json.dumps(self._findings_doc()))

        mock_client = MagicMock()
        mock_client.get_pr.return_value = {"reviewers": []}
        mock_client.get_threads.return_value = []

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("VOTE_WAITING_ON", raising=False)
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 0
        result = json.loads(out_file.read_text())
        assert result["created"] == 0
        assert result["votedWaitingForAuthor"] is False

    def test_finding_is_posted_as_thread(self, tmp_path, monkeypatch):
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        findings_file.write_text(
            json.dumps(
                self._findings_doc(
                    findings=[
                        {
                            "severity": "major",
                            "title": "Leak",
                            "message": "Token in log",
                            "file": "src/log.ts",
                            "line": 10,
                        }
                    ]
                )
            )
        )

        mock_client = MagicMock()
        mock_client.get_pr.return_value = {"reviewers": []}
        mock_client.get_threads.return_value = []
        mock_client.create_thread.return_value = {"id": 99}

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.delenv("FAIL_ON", raising=False)
        monkeypatch.setenv("VOTE_WAITING_ON", "none")

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 0
        mock_client.create_thread.assert_called_once()
        result = json.loads(out_file.read_text())
        assert result["created"] == 1

    def test_duplicate_finding_skipped(self, tmp_path, monkeypatch):
        finding = {
            "severity": "major",
            "title": "Dup",
            "message": "Already posted",
            "file": None,
            "line": None,
        }
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        findings_file.write_text(json.dumps(self._findings_doc(findings=[finding])))

        # Pre-compute the key that would be generated
        normalized = {
            "file": None,
            "line": None,
            "severity": "major",
            "title": "Dup",
            "message": "Already posted",
        }
        key = m.key_of(normalized)
        existing_comment = f"prb:{key}"

        mock_client = MagicMock()
        mock_client.get_pr.return_value = {"reviewers": []}
        mock_client.get_threads.return_value = [
            {"comments": [{"content": existing_comment}]}
        ]

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("VOTE_WAITING_ON", "none")
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 0
        mock_client.create_thread.assert_not_called()
        result = json.loads(out_file.read_text())
        assert result["skipped"] == 1

    def test_fail_on_blocker_returns_nonzero(self, tmp_path, monkeypatch):
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        findings_file.write_text(
            json.dumps(
                self._findings_doc(
                    findings=[
                        {"severity": "blocker", "title": "T", "message": "M"}
                    ]
                )
            )
        )

        mock_client = MagicMock()
        mock_client.get_pr.return_value = {"reviewers": []}
        mock_client.get_threads.return_value = []
        mock_client.create_thread.return_value = {"id": 1}

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("FAIL_ON", "blocker")
        monkeypatch.setenv("VOTE_WAITING_ON", "none")

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 1

    def test_max_findings_cap_applied(self, tmp_path, monkeypatch):
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        # 5 major findings; cap to 3
        findings_file.write_text(
            json.dumps(
                self._findings_doc(
                    findings=[
                        {"severity": "major", "title": f"F{i}", "message": "M"}
                        for i in range(5)
                    ]
                )
            )
        )

        mock_client = MagicMock()
        mock_client.get_pr.return_value = {"reviewers": []}
        mock_client.get_threads.return_value = []
        mock_client.create_thread.return_value = {"id": 1}

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("MAX_FINDINGS", "3")
        monkeypatch.setenv("VOTE_WAITING_ON", "none")
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 0
        result = json.loads(out_file.read_text())
        assert result["created"] == 3

    def test_vote_waiting_on_major_casts_vote(self, tmp_path, monkeypatch):
        findings_file = tmp_path / "findings.json"
        out_file = tmp_path / "result.json"
        findings_file.write_text(
            json.dumps(
                self._findings_doc(
                    findings=[{"severity": "major", "title": "T", "message": "M"}]
                )
            )
        )

        reviewer_id = "rev-id-1"
        mock_client = MagicMock()
        mock_client.get_pr.return_value = {
            "reviewers": [{"id": reviewer_id, "uniqueName": "bot@x.com"}]
        }
        mock_client.get_threads.return_value = []
        mock_client.create_thread.return_value = {"id": 5}
        mock_client.connection_data.return_value = {
            "authenticatedUser": {"id": reviewer_id, "uniqueName": "bot@x.com"}
        }

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        monkeypatch.setenv("VOTE_WAITING_ON", "major")
        monkeypatch.delenv("FAIL_ON", raising=False)

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_post_findings(self._args(findings_file, out_file))

        assert rc == 0
        mock_client.vote.assert_called_once_with(42, reviewer_id, m.VOTE_WAITING)
        result = json.loads(out_file.read_text())
        assert result["votedWaitingForAuthor"] is True


# ---------------------------------------------------------------------------
# command_fetch_context (integration with mocked ADO client)
# ---------------------------------------------------------------------------


class TestCommandFetchContext:
    def _args(self, out_dir):
        return SimpleNamespace(
            org="contoso",
            project="Payments",
            repo="api",
            pr=42,
            out=str(out_dir),
        )

    def test_writes_expected_files(self, tmp_path, monkeypatch):
        pr_data = {
            "title": "Add feature",
            "description": "Details",
            "status": "active",
            "isDraft": False,
            "sourceRefName": "refs/heads/feature/x",
            "targetRefName": "refs/heads/main",
            "createdBy": None,
            "reviewers": [],
            "workItemRefs": [],
        }
        mock_client = MagicMock()
        mock_client.get_pr.return_value = pr_data
        mock_client.get_threads.return_value = []
        mock_client.org_name = "contoso"

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.command_fetch_context(self._args(tmp_path))

        assert rc == 0
        for filename in ("metadata.json", "work-items.json", "work-item-comments.json", "threads.json", "context.json"):
            assert (tmp_path / filename).exists(), f"{filename} not written"

    def test_metadata_fields(self, tmp_path, monkeypatch):
        pr_data = {
            "title": "My PR",
            "description": "desc",
            "status": "active",
            "isDraft": False,
            "sourceRefName": "refs/heads/feat",
            "targetRefName": "refs/heads/main",
            "createdBy": {"displayName": "Dev"},
            "reviewers": [],
            "workItemRefs": [],
        }
        mock_client = MagicMock()
        mock_client.get_pr.return_value = pr_data
        mock_client.get_threads.return_value = []
        mock_client.org_name = "contoso"

        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            m.command_fetch_context(self._args(tmp_path))

        metadata = json.loads((tmp_path / "metadata.json").read_text())
        assert metadata["title"] == "My PR"
        assert metadata["status"] == "active"
        assert metadata["pullRequestId"] == 42


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------


class TestCli:
    def test_missing_command_exits_nonzero(self):
        with pytest.raises(SystemExit) as exc_info:
            m.main([])
        assert exc_info.value.code != 0

    def test_fetch_context_command_parsed(self, tmp_path, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "tok")
        mock_client = MagicMock()
        mock_client.get_pr.return_value = {
            "title": "",
            "description": "",
            "status": "active",
            "isDraft": False,
            "sourceRefName": "refs/heads/feat",
            "targetRefName": "refs/heads/main",
            "createdBy": None,
            "reviewers": [],
            "workItemRefs": [],
        }
        mock_client.get_threads.return_value = []
        mock_client.org_name = "contoso"

        with patch("reviewforge.ado.cli.AdoClient", return_value=mock_client):
            rc = m.main(
                [
                    "fetch-context",
                    "--org", "contoso",
                    "--project", "Payments",
                    "--repo", "api",
                    "--pr", "1",
                    "--out", str(tmp_path),
                ]
            )
        assert rc == 0


class TestAdditionalCoverage:
    def test_token_missing_raises(self, monkeypatch):
        monkeypatch.delenv('ADO_AUTH_TOKEN', raising=False)
        monkeypatch.delenv('ADO_MCP_AUTH_TOKEN', raising=False)
        monkeypatch.delenv("SYSTEM_ACCESSTOKEN", raising=False)
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        with pytest.raises(SystemExit):
            m.token()

    def test_enc_quotes_values(self):
        assert m.enc('a b') == 'a%20b'

    def test_client_initializes_with_token_and_normalized_org(self, monkeypatch):
        monkeypatch.setenv('ADO_AUTH_TOKEN', 'tok')
        client = m.AdoClient('contoso', 'Proj', 'Repo')
        assert client.org_url == 'https://dev.azure.com/contoso'
        assert client.base.endswith('/Proj')

    def test_fetch_work_items_without_refs_returns_empty(self):
        client = MagicMock()
        items, comments = m.fetch_work_items(client, {'workItemRefs': []})
        assert items == [] and comments == []

    def test_fetch_work_items_with_refs(self):
        client = MagicMock()
        client.post.return_value = {
            "value": [
                {
                    "id": 100,
                    "fields": {
                        "System.WorkItemType": "User Story",
                        "System.Title": "Login flow",
                        "System.State": "Active",
                    },
                }
            ]
        }
        client.get.return_value = {
            "comments": [
                {"id": 1, "author": {"displayName": "alice"}, "text": "hi"}
            ]
        }
        pr = {"workItemRefs": [{"id": 100}]}
        items, comments = m.fetch_work_items(client, pr)
        assert items[0]["title"] == "Login flow"
        assert items[0]["type"] == "User Story"
        assert comments[0]["workItemId"] == "100"
        assert comments[0]["comments"][0]["author"] == "alice"


# ---------------------------------------------------------------------------
# _filter_findings (POST_MIN_SEVERITY, DROP_LOW_CONFIDENCE, REQUIRE_CONTEXT_FOR, MAX_FINDINGS)
# ---------------------------------------------------------------------------


class TestFilterFindings:
    def _f(self, severity, **extras):
        base = {"severity": severity, "title": f"t-{severity}"}
        base.update(extras)
        return base

    @pytest.fixture(autouse=True)
    def _clean_filter_env(self, monkeypatch):
        # _filter_findings reads POST_MIN_SEVERITY / DROP_LOW_CONFIDENCE /
        # REQUIRE_CONTEXT_FOR / MAX_FINDINGS directly from os.environ; isolate
        # tests from each other and from a real .env.
        for k in (
            "POST_MIN_SEVERITY",
            "DROP_LOW_CONFIDENCE",
            "REQUIRE_CONTEXT_FOR",
            "MAX_FINDINGS",
        ):
            monkeypatch.delenv(k, raising=False)

    def test_post_min_severity_drops_lower(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "major")
        out = m._filter_findings([self._f("minor"), self._f("major"), self._f("blocker")])
        assert [f["severity"] for f in out] == ["major", "blocker"]

    def test_post_min_severity_lowest_keeps_all(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        out = m._filter_findings([self._f("nit"), self._f("minor"), self._f("major")])
        assert len(out) == 3

    def test_post_min_severity_invalid_exits(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "bogus")
        with pytest.raises(SystemExit):
            m._filter_findings([self._f("minor")])

    def test_drop_low_confidence(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("DROP_LOW_CONFIDENCE", "1")
        out = m._filter_findings([
            self._f("major", confidence="low"),
            self._f("major", confidence="high"),
        ])
        assert len(out) == 1
        assert out[0]["confidence"] == "high"

    def test_require_context_for_drops_without_context(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("REQUIRE_CONTEXT_FOR", "blocker")
        out = m._filter_findings([
            self._f("blocker"),  # no context, no basis → dropped
            self._f("blocker", evidence={"contextFilesRead": ["x.py"]}),
        ])
        assert len(out) == 1
        assert out[0]["evidence"]["contextFilesRead"] == ["x.py"]

    def test_require_context_for_invalid_exits(self, monkeypatch):
        monkeypatch.setenv("REQUIRE_CONTEXT_FOR", "bogus")
        with pytest.raises(SystemExit):
            m._filter_findings([self._f("minor")])

    def test_require_context_basis_keeps_finding(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("REQUIRE_CONTEXT_FOR", "blocker")
        out = m._filter_findings([
            self._f("blocker", contextBasis="surrounding-code-read"),
        ])
        assert len(out) == 1

    def test_max_findings_caps(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("MAX_FINDINGS", "2")
        out = m._filter_findings([
            self._f("nit"), self._f("minor"), self._f("major"), self._f("blocker"),
        ])
        # Sorted by severity desc, top 2 = blocker + major
        assert [f["severity"] for f in out] == ["blocker", "major"]

    def test_max_findings_negative_exits(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("MAX_FINDINGS", "-1")
        with pytest.raises(SystemExit):
            m._filter_findings([self._f("minor")])

    def test_max_findings_invalid_exits(self, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "nit")
        monkeypatch.setenv("MAX_FINDINGS", "notanumber")
        with pytest.raises(SystemExit):
            m._filter_findings([self._f("minor")])


# ---------------------------------------------------------------------------
# token() / org() / project() / repo() env shims
# ---------------------------------------------------------------------------


class TestEnvShims:
    """Compatibility helpers retained for external consumers."""


    def test_token_resolves_canonical(self, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "primary")
        monkeypatch.delenv("SYSTEM_ACCESSTOKEN", raising=False)
        assert m.token() == "primary"

    def test_token_resolves_system_access_token_first(self, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "primary")
        monkeypatch.setenv("SYSTEM_ACCESSTOKEN", "sys")
        assert m.token() == "sys"

    def test_token_falls_back_to_mcp(self, monkeypatch):
        for k in ("ADO_AUTH_TOKEN", "SYSTEM_ACCESSTOKEN"):
            monkeypatch.delenv(k, raising=False)
        monkeypatch.setenv("ADO_MCP_AUTH_TOKEN", "mcp-tok")
        assert m.token() == "mcp-tok"

    def test_token_falls_back_to_api_key(self, monkeypatch):
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "SYSTEM_ACCESSTOKEN"):
            monkeypatch.delenv(k, raising=False)
        monkeypatch.setenv("ADO_API_KEY", "api-key-tok")
        assert m.token() == "api-key-tok"

    def test_token_missing_exits(self, monkeypatch):
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY", "SYSTEM_ACCESSTOKEN"):
            monkeypatch.delenv(k, raising=False)
        with pytest.raises(SystemExit):
            m.token()

    def test_org_returns_env_value(self, monkeypatch):
        monkeypatch.setenv("ADO_ORG", "contoso")
        assert m.org() == "contoso"

    def test_org_missing_exits(self, monkeypatch):
        monkeypatch.delenv("ADO_ORG", raising=False)
        with pytest.raises(SystemExit):
            m.org()

    def test_project_returns_env_value(self, monkeypatch):
        monkeypatch.setenv("ADO_PROJECT", "Pay")
        assert m.project() == "Pay"

    def test_project_missing_exits(self, monkeypatch):
        monkeypatch.delenv("ADO_PROJECT", raising=False)
        with pytest.raises(SystemExit):
            m.project()

    def test_repo_returns_env_value(self, monkeypatch):
        monkeypatch.setenv("ADO_REPO_ID", "api")
        assert m.repo() == "api"

    def test_repo_missing_exits(self, monkeypatch):
        monkeypatch.delenv("ADO_REPO_ID", raising=False)
        with pytest.raises(SystemExit):
            m.repo()
