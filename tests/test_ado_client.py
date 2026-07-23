"""HTTP-level tests for the :class:`AdoClient` REST wrapper.

The tests patch :func:`urllib.request.urlopen` so the network is never
touched. Each public method (``get_pr``, ``get_threads``, ``create_thread``,
``vote``, ``connection_data``) is exercised for the happy path and the
basic error path.
"""
from __future__ import annotations

import json
import sys
from io import BytesIO
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from reviewforge.ado.client import (  # noqa: E402
    AdoClient,
    _normalize_org,
    call_helper,
    get_pr,
    normalize_ado_segment,
    normalize_branch_name,
    parse_pr_url,
    resolve_branches,
    resolve_token,
)
from reviewforge.exceptions import AdoApiError  # noqa: E402


# ---------------------------------------------------------------------------
# Test doubles
# ---------------------------------------------------------------------------


def _http_response(payload):
    """Build a context-manager-compatible mock response for urlopen."""
    body = json.dumps(payload).encode("utf-8") if payload is not None else b""
    resp = MagicMock()
    resp.read.return_value = body
    resp.__enter__.return_value = resp
    resp.__exit__.return_value = False
    return resp


def _patch_urlopen(payload=None, error=None):
    """Return a ``patch`` object for ``urllib.request.urlopen``."""
    target = "reviewforge.ado.client.urllib.request.urlopen"

    if error is not None:
        return patch(target, side_effect=error)
    return patch(target, return_value=_http_response(payload))


# ---------------------------------------------------------------------------
# _normalize_org
# ---------------------------------------------------------------------------


class TestNormalizeOrg:
    def test_short_name(self):
        url, short = _normalize_org("contoso")
        assert url == "https://dev.azure.com/contoso"
        assert short == "contoso"

    def test_dev_azure_url(self):
        url, short = _normalize_org("https://dev.azure.com/contoso")
        assert url == "https://dev.azure.com/contoso"
        assert short == "contoso"

    def test_dev_azure_url_trailing_slash(self):
        url, short = _normalize_org("https://dev.azure.com/contoso/")
        assert "contoso" in url
        assert short == "contoso"

    def test_visualstudio_url(self):
        url, short = _normalize_org("https://contoso.visualstudio.com")
        assert url == "https://contoso.visualstudio.com"
        assert short == "contoso"

    def test_unknown_url_raises(self):
        with pytest.raises(AdoApiError):
            _normalize_org("https://example.com/foo")

    def test_short_name_with_dot_raises(self):
        with pytest.raises(AdoApiError):
            _normalize_org("contoso.example.com")


# ---------------------------------------------------------------------------
# normalize_ado_segment (migrated from PowerShell Normalize-AdoSegment)
# ---------------------------------------------------------------------------


class TestNormalizeAdoSegment:
    def test_short_name(self):
        assert normalize_ado_segment("contoso", "org") == "contoso"

    def test_strips_trailing_slash(self):
        assert normalize_ado_segment("contoso/", "org") == "contoso"

    def test_strips_whitespace(self):
        assert normalize_ado_segment("  contoso  ", "org") == "contoso"

    def test_rejects_url(self):
        with pytest.raises(ValueError, match="must be the short Azure DevOps name"):
            normalize_ado_segment("https://dev.azure.com/contoso", "org")

    def test_rejects_empty(self):
        with pytest.raises(ValueError, match="required"):
            normalize_ado_segment("", "org")

    def test_rejects_whitespace_only(self):
        with pytest.raises(ValueError, match="required"):
            normalize_ado_segment("   ", "org")

    def test_rejects_none(self):
        with pytest.raises(ValueError, match="required"):
            normalize_ado_segment(None, "org")

    def test_rejects_newlines(self):
        # CR/LF injection defense.
        with pytest.raises(ValueError, match="line breaks"):
            normalize_ado_segment("contoso\r\nX-Injected: true", "org")

    def test_preserves_dots_underscores_dashes(self):
        # Project / repo names commonly contain these.
        assert normalize_ado_segment("my.project_name-2", "project") == "my.project_name-2"

    def test_includes_name_in_error(self):
        with pytest.raises(ValueError, match="ADO organization"):
            normalize_ado_segment("", "ADO organization")


# ---------------------------------------------------------------------------
# normalize_branch_name (migrated from PowerShell Normalize-BranchName)
# ---------------------------------------------------------------------------


class TestNormalizeBranchName:
    def test_strips_refs_heads(self):
        assert normalize_branch_name("refs/heads/main") == "main"

    def test_passes_through_short_name(self):
        assert normalize_branch_name("main") == "main"

    def test_passes_through_nested_branch(self):
        # Nested branches keep their structure.
        assert normalize_branch_name("refs/heads/feature/foo") == "feature/foo"

    def test_only_strips_at_start(self):
        # Doesn't accidentally strip ``refs/heads/`` from the middle.
        assert normalize_branch_name("refs/heads/refs/heads/x") == "refs/heads/x"

    def test_empty_branch(self):
        assert normalize_branch_name("") == ""

    def test_none_branch(self):
        assert normalize_branch_name(None) is None


# ---------------------------------------------------------------------------
# AdoClient construction
# ---------------------------------------------------------------------------


class TestAdoClientConstruction:
    def test_uses_explicit_token(self, monkeypatch):
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        client = AdoClient("contoso", "P", "r", token="explicit")
        assert client.org_name == "contoso"
        assert client.org_url == "https://dev.azure.com/contoso"
        assert client.base.endswith("/P")
        assert client.token == "explicit"

    def test_falls_back_to_env_token(self, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "env-token")
        client = AdoClient("contoso", "P", "r")
        assert client.token == "env-token"


# ---------------------------------------------------------------------------
# resolve_token
# ---------------------------------------------------------------------------


class TestResolveToken:
    def test_reads_primary_name(self, monkeypatch):
        monkeypatch.setenv("ADO_AUTH_TOKEN", "primary")
        monkeypatch.delenv("ADO_MCP_AUTH_TOKEN", raising=False)
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        assert resolve_token() == "primary"

    def test_falls_back_to_ado_mcp_auth_token(self, monkeypatch):
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.setenv("ADO_MCP_AUTH_TOKEN", "mcp")
        monkeypatch.delenv("ADO_API_KEY", raising=False)
        assert resolve_token() == "mcp"

    def test_falls_back_to_ado_api_key(self, monkeypatch):
        monkeypatch.delenv("ADO_AUTH_TOKEN", raising=False)
        monkeypatch.delenv("ADO_MCP_AUTH_TOKEN", raising=False)
        monkeypatch.setenv("ADO_API_KEY", "apikey")
        assert resolve_token() == "apikey"

    def test_raises_when_all_missing(self, monkeypatch):
        for k in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
            monkeypatch.delenv(k, raising=False)
        with pytest.raises(AdoApiError):
            resolve_token()


# ---------------------------------------------------------------------------
# parse_pr_url
# ---------------------------------------------------------------------------


class TestParsePrUrl:
    def test_dev_azure(self):
        assert parse_pr_url("https://dev.azure.com/contoso/Pay/_git/api/pullrequest/7") == (
            "contoso", "Pay", "api", "7",
        )

    def test_visualstudio(self):
        assert parse_pr_url("https://contoso.visualstudio.com/Pay/_git/api/pullrequest/9") == (
            "contoso", "Pay", "api", "9",
        )

    def test_unparseable(self):
        with pytest.raises(AdoApiError):
            parse_pr_url("https://example.com/pull/1")


# ---------------------------------------------------------------------------
# HTTP methods
# ---------------------------------------------------------------------------


class TestHttpMethods:
    def test_get_pr(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        pr = {"pullRequestId": 42, "status": "active"}
        with _patch_urlopen(pr):
            out = client.get_pr(42)
        assert out["pullRequestId"] == 42
        assert out["status"] == "active"

    def test_get_pr_with_work_item_refs(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"pullRequestId": 1, "workItemRefs": [{"id": 99}]}):
            out = client.get_pr(1, include_work_item_refs=True)
        assert out["workItemRefs"] == [{"id": 99}]

    def test_get_threads(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"value": [{"id": 1}, {"id": 2}]}):
            threads = client.get_threads(1)
        assert threads == [{"id": 1}, {"id": 2}]

    def test_create_thread(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"id": 99}):
            resp = client.create_thread(1, {"comments": [{"content": "hi"}]})
        assert resp == {"id": 99}

    def test_vote(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"vote": -5}):
            resp = client.vote(1, "rev-1", -5)
        assert resp == {"vote": -5}

    def test_connection_data(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"authenticatedUser": {"id": "u1"}}):
            out = client.connection_data()
        assert out["authenticatedUser"]["id"] == "u1"

    def test_empty_response(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen(None):  # empty body
            assert client.get_pr(1) == {}

    def test_http_error_propagates(self, monkeypatch):
        from urllib.error import HTTPError
        client = AdoClient("contoso", "P", "api", token="t")
        err = HTTPError(url="x", code=500, msg="boom", hdrs={}, fp=BytesIO(b""))
        with _patch_urlopen(error=err):
            with pytest.raises(AdoApiError):
                client.get_pr(1)

    def test_get_retries_503_then_succeeds(self):
        from urllib.error import HTTPError

        error = HTTPError(url="x", code=503, msg="busy", hdrs={}, fp=BytesIO(b""))
        sleeper = MagicMock()
        client = AdoClient(
            "contoso", "P", "api", token="t", sleeper=sleeper, random_fn=lambda: 0
        )
        with patch(
            "reviewforge.ado.client.urllib.request.urlopen",
            side_effect=[error, _http_response({"ok": True})],
        ) as urlopen:
            assert client.get_pr(1) == {"ok": True}
        assert urlopen.call_count == 2
        sleeper.assert_called_once_with(0.5)

    def test_429_honors_retry_after(self):
        from email.message import Message
        from urllib.error import HTTPError

        headers = Message()
        headers["Retry-After"] = "7"
        error = HTTPError(url="x", code=429, msg="slow", hdrs=headers, fp=BytesIO(b""))
        sleeper = MagicMock()
        client = AdoClient("contoso", "P", "api", token="t", sleeper=sleeper)
        with patch(
            "reviewforge.ado.client.urllib.request.urlopen",
            side_effect=[error, _http_response({"ok": True})],
        ):
            assert client.get_pr(1) == {"ok": True}
        sleeper.assert_called_once_with(7)

    def test_401_fails_without_retry(self):
        from urllib.error import HTTPError

        error = HTTPError(url="x", code=401, msg="unauthorized", hdrs={}, fp=BytesIO(b""))
        client = AdoClient("contoso", "P", "api", token="t", sleeper=MagicMock())
        with patch("reviewforge.ado.client.urllib.request.urlopen", side_effect=error) as urlopen:
            with pytest.raises(AdoApiError):
                client.get_pr(1)
        assert urlopen.call_count == 1

    def test_post_500_fails_without_retry(self):
        from urllib.error import HTTPError

        error = HTTPError(url="x", code=500, msg="server", hdrs={}, fp=BytesIO(b""))
        client = AdoClient("contoso", "P", "api", token="t", sleeper=MagicMock())
        with patch("reviewforge.ado.client.urllib.request.urlopen", side_effect=error) as urlopen:
            with pytest.raises(AdoApiError):
                client.create_thread(1, {"comments": []})
        assert urlopen.call_count == 1

    def test_get_retries_url_error(self):
        from urllib.error import URLError

        client = AdoClient(
            "contoso", "P", "api", token="t", sleeper=MagicMock(), random_fn=lambda: 0
        )
        with patch(
            "reviewforge.ado.client.urllib.request.urlopen",
            side_effect=[URLError("reset"), _http_response({"ok": True})],
        ) as urlopen:
            assert client.get_pr(1) == {"ok": True}
        assert urlopen.call_count == 2

    def test_pr_path_encodes_repo_id(self):
        client = AdoClient("contoso", "P", "a/b", token="t")
        path = client.pr_path(1)
        # repo id with a slash is percent-encoded.
        assert "a%2Fb" in path

    def test_get_post_put_helpers(self, monkeypatch):
        client = AdoClient("contoso", "P", "api", token="t")
        with _patch_urlopen({"ok": 1}):
            assert client.get("/path") == {"ok": 1}
        with _patch_urlopen({"posted": True}):
            assert client.post("/path", {"x": 1}) == {"posted": True}
        with _patch_urlopen({"put": True}):
            assert client.put("/path", {"x": 1}) == {"put": True}


# ---------------------------------------------------------------------------
# get_pr / resolve_branches (module-level)
# ---------------------------------------------------------------------------


class TestModuleHelpers:
    def test_get_pr_uses_config(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="1",
            ado_token="t", source_branch="", target_branch="",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        with _patch_urlopen({"pullRequestId": 1}):
            out = get_pr(cfg)
        assert out["pullRequestId"] == 1

    def test_resolve_branches_uses_config(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="1",
            ado_token="t", source_branch="refs/heads/feature", target_branch="refs/heads/main",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        assert resolve_branches(cfg) == ("feature", "main")

    def test_resolve_branches_falls_back_to_api(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="1",
            ado_token="t", source_branch="", target_branch="",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        monkeypatch.setattr(
            "reviewforge.ado.client.get_pr",
            lambda c: {"sourceRefName": "refs/heads/s", "targetRefName": "refs/heads/t"},
        )
        assert resolve_branches(cfg) == ("s", "t")

    def test_resolve_branches_raises_when_api_incomplete(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="1",
            ado_token="t", source_branch="", target_branch="",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        monkeypatch.setattr(
            "reviewforge.ado.client.get_pr",
            lambda c: {"sourceRefName": "refs/heads/s"},  # missing target
        )
        with pytest.raises(AdoApiError):
            resolve_branches(cfg)


# ---------------------------------------------------------------------------
# call_helper
# ---------------------------------------------------------------------------


class TestCallHelper:
    def test_call_helper_builds_fetch_context_command(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="42",
            ado_token="t", source_branch="s", target_branch="t",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        import subprocess as _sp
        captured = []

        def fake_run(args, stdout, stderr):
            captured.append(args)
            return _sp.CompletedProcess(args, 0, b"", b"")

        monkeypatch.setattr("reviewforge.ado.client.subprocess.run", fake_run)
        call_helper(cfg, "fetch-context", tmp_path)
        # The last two args are --out <path>.
        assert captured[0][-2:] == ["--out", str(tmp_path)]
        # The third positional is the subcommand.
        assert captured[0][3] == "fetch-context"

    def test_call_helper_builds_post_findings_command(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="42",
            ado_token="t", source_branch="s", target_branch="t",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        import subprocess as _sp
        captured = []

        def fake_run(args, stdout, stderr):
            captured.append(args)
            return _sp.CompletedProcess(args, 0, b"", b"")

        monkeypatch.setattr("reviewforge.ado.client.subprocess.run", fake_run)
        findings = tmp_path / "findings.json"
        findings.write_text("{}", encoding="utf-8")
        call_helper(cfg, "post-findings", tmp_path, findings=findings)
        # --findings + --out point at the right files.
        assert "--findings" in captured[0]
        assert str(findings) in captured[0]
        assert captured[0][3] == "post-findings"

    def test_call_helper_raises_on_failure(self, tmp_path, monkeypatch):
        from reviewforge.config import Config
        cfg = Config(
            ado_org="contoso", ado_project="P", ado_repo_id="api", pr_id="42",
            ado_token="t", source_branch="s", target_branch="t",
            workspace=tmp_path, clone_root=tmp_path, review_language="English",
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False, review_target_branches="",
            review_artifact_dir=None, review_artifact_root=tmp_path, review_run_id=None,
        )
        import subprocess as _sp
        monkeypatch.setattr(
            "reviewforge.ado.client.subprocess.run",
            lambda *a, **k: _sp.CompletedProcess(a, 2, b"", b"boom"),
        )
        with pytest.raises(AdoApiError):
            call_helper(cfg, "fetch-context", tmp_path)


# ---------------------------------------------------------------------------
# list_active_pull_requests
# ---------------------------------------------------------------------------


class TestListActivePullRequests:
    def _cfg(self, **overrides):
        from reviewforge.config import Config
        defaults = dict(
            ado_org="contoso", ado_project="Pay", ado_repo_id="api", pr_id="1",
            ado_token="t", source_branch="s", target_branch="main",
            review_language="English",
            pi_model="m", max_diff_bytes=1, chunk_trigger_diff_bytes=1,
            disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
            include_work_items=True, include_existing_comments=True,
            verify_findings=True, force_review=False,
            review_target_branches="",
            review_artifact_dir=None, review_run_id=None,
        )
        defaults.update(overrides)
        from reviewforge.config import Config
        return Config(**defaults)

    def test_returns_empty_when_no_prs(self, tmp_path, monkeypatch):
        from reviewforge.ado.client import list_active_pull_requests
        cfg = self._cfg(
            workspace=tmp_path, clone_root=tmp_path,
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            review_artifact_root=tmp_path,
        )
        with patch.object(AdoClient, "_request", return_value={"value": []}):
            out = list_active_pull_requests(cfg, project="Pay")
        assert out == []

    def test_returns_all_prs(self, tmp_path, monkeypatch):
        from reviewforge.ado.client import list_active_pull_requests
        cfg = self._cfg(
            workspace=tmp_path, clone_root=tmp_path,
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            review_artifact_root=tmp_path,
        )
        page = {
            "value": [
                {"pullRequestId": 1, "targetRefName": "refs/heads/main"},
                {"pullRequestId": 2, "targetRefName": "refs/heads/develop"},
            ]
        }
        with patch.object(AdoClient, "_request", return_value=page):
            out = list_active_pull_requests(cfg, project="Pay")
        assert [p["pullRequestId"] for p in out] == [1, 2]
        assert all(p["project"] == "Pay" for p in out)

    def test_filters_by_target_branches(self, tmp_path, monkeypatch):
        from reviewforge.ado.client import list_active_pull_requests
        cfg = self._cfg(
            workspace=tmp_path, clone_root=tmp_path,
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            review_artifact_root=tmp_path,
        )
        page = {
            "value": [
                {"pullRequestId": 1, "targetRefName": "refs/heads/main"},
                {"pullRequestId": 2, "targetRefName": "refs/heads/feature/x"},
            ]
        }
        with patch.object(AdoClient, "_request", return_value=page):
            out = list_active_pull_requests(
                cfg, project="Pay", target_branches=["main", "develop"]
            )
        assert [p["pullRequestId"] for p in out] == [1]

    def test_respects_max_results_cap(self, tmp_path, monkeypatch):
        from reviewforge.ado.client import list_active_pull_requests
        cfg = self._cfg(
            workspace=tmp_path, clone_root=tmp_path,
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            review_artifact_root=tmp_path,
        )
        page = {
            "value": [
                {"pullRequestId": i, "targetRefName": "refs/heads/main"}
                for i in range(1, 4)
            ]
        }
        with patch.object(AdoClient, "_request", return_value=page):
            out = list_active_pull_requests(cfg, project="Pay", max_results=2)
        assert len(out) == 2

    def test_paginates(self, tmp_path, monkeypatch):
        from reviewforge.ado.client import list_active_pull_requests
        cfg = self._cfg(
            workspace=tmp_path, clone_root=tmp_path,
            review_prompt_path=tmp_path / "r.md", intent_prompt_path=tmp_path / "i.md",
            context_plan_prompt_path=tmp_path / "p.md", context_digest_prompt_path=tmp_path / "d.md",
            verify_prompt_path=tmp_path / "v.md", severity_prompt_path=tmp_path / "s.md",
            standards_path=tmp_path / "std.md",
            review_artifact_root=tmp_path,
        )
        # Two pages: first full, second short.
        full_page = {
            "value": [
                {"pullRequestId": i, "targetRefName": "refs/heads/main"}
                for i in range(100)
            ]
        }
        second_page = {
            "value": [
                {"pullRequestId": 100, "targetRefName": "refs/heads/main"}
            ]
        }
        responses = [full_page, second_page]
        with patch.object(AdoClient, "_request", side_effect=responses):
            out = list_active_pull_requests(cfg, project="Pay")
        assert len(out) == 101
