"""End-to-end integration tests for the review pipeline.

These tests run the real pipeline stages (orchestrator → fetch → git →
reasoning → anchors → posting) with only two boundaries stubbed:

* Pi subprocess calls (``PiRunner.run_json``) return canned JSON.
* ADO HTTP (``urllib.request.urlopen``) is answered by an in-memory router
  that records every request.

Git runs for real against a local ``file://`` repository, so the clone /
merge-base / diff path is exercised without network access.
"""
from __future__ import annotations

import io
import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from reviewforge.config import Config  # noqa: E402
from reviewforge.git import ops as git_ops  # noqa: E402
from reviewforge.pipeline import orchestrator  # noqa: E402

# Env vars that operations.py reads directly; cleared so the host
# environment cannot leak into a run.
ENV_ISOLATION = [
    "FAIL_ON",
    "VOTE_WAITING_ON",
    "POST_MIN_SEVERITY",
    "DROP_LOW_CONFIDENCE",
    "REQUIRE_CONTEXT_FOR",
    "MAX_FINDINGS",
    "ANNOTATE_STALE",
]

PR_PAYLOAD = {
    "pullRequestId": 42,
    "title": "Feature work",
    "description": "changes things",
    "status": "active",
    "isDraft": False,
    "sourceRefName": "refs/heads/feature",
    "targetRefName": "refs/heads/main",
    "lastMergeSourceCommit": {"commitId": "0" * 40},
    "reviewers": [],
}



def _artifacts_dir(cfg: Config) -> Path:
    return cfg.review_artifact_root / "pr-42" / "runs" / cfg.review_run_id

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def clean_env(monkeypatch):
    for name in ENV_ISOLATION:
        monkeypatch.delenv(name, raising=False)


@pytest.fixture
def cfg(tmp_path: Path, clean_env) -> Config:
    prompts = tmp_path / "prompts"
    prompts.mkdir()
    files: dict[str, Path] = {}
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "fast-review", "standards"]:
        files[name] = prompts / f"{name}.md"
        files[name].write_text(f"{name} prompt", encoding="utf-8")
    return Config(
        ado_org="contoso",
        ado_project="P",
        ado_repo_id="r",
        pr_id="42",
        ado_token="tok",
        source_branch="feature",
        target_branch="main",
        workspace=tmp_path / "workspace",
        clone_root=tmp_path / "workspace",
        review_language="English",
        review_prompt_path=files["review"],
        intent_prompt_path=files["intent"],
        context_plan_prompt_path=files["plan"],
        context_digest_prompt_path=files["digest"],
        verify_prompt_path=files["verify"],
        severity_prompt_path=files["severity"],
        fast_review_prompt_path=files["fast-review"],
        standards_path=files["standards"],
        pi_model="m",
        max_diff_bytes=100_000,
        chunk_trigger_diff_bytes=100_000,
        disable_chunk_review=False,
        pi_timeout_secs=5,
        dry_run=False,
        include_work_items=True,
        include_existing_comments=True,
        verify_findings=True,
        force_review=False,
        review_target_branches="",
        review_artifact_dir=None,
        review_artifact_root=tmp_path / "artifacts",
        review_run_id="run-1",
    )


def _git(repo: Path, *args: str) -> str:
    cp = subprocess.run(
        ["git", "-C", str(repo), *args],
        check=True,
        capture_output=True,
        text=True,
    )
    return cp.stdout


@pytest.fixture
def git_repo(tmp_path: Path, monkeypatch) -> Path:
    """Real local repo: main with 5-line file, feature changes line 3."""
    repo = tmp_path / "remote"
    repo.mkdir()
    _git(repo, "init", "-b", "main")
    _git(repo, "config", "user.email", "t@example.com")
    _git(repo, "config", "user.name", "T")
    src = repo / "src"
    src.mkdir()
    (src / "app.py").write_text("a1\na2\na3\na4\na5\n", encoding="utf-8")
    (src / "other.py").write_text("o1\no2\no3\n", encoding="utf-8")
    _git(repo, "add", ".")
    _git(repo, "commit", "-m", "base")
    _git(repo, "checkout", "-b", "feature")
    (src / "app.py").write_text("a1\na2\nCHANGED\na4\na5\n", encoding="utf-8")
    (src / "other.py").write_text("o1\nOTHER-CHANGED\no3\n", encoding="utf-8")
    _git(repo, "commit", "-am", "change two files")
    # Redirect HOME so prepare_repo's `git config --global safe.directory`
    # does not touch the developer's real global git config.
    monkeypatch.setenv("HOME", str(tmp_path / "home"))
    monkeypatch.setattr(git_ops, "_repo_url", lambda _cfg: f"file://{repo}")
    return repo


class AdoStub:
    """In-memory ADO REST router recording every request."""

    def __init__(self, threads: list[dict] | None = None):
        self.requests: list[tuple[str, str, dict | None]] = []
        self.threads = threads if threads is not None else []
        self.created_threads: list[dict] = []

    def __call__(self, req, *args, **kwargs):
        url = req.full_url
        method = req.get_method()
        body = json.loads(req.data.decode()) if req.data else None
        self.requests.append((method, url, body))
        if "pullRequests/42/threads" in url and method == "POST":
            self.created_threads.append(body)
            payload: dict = {"id": 100 + len(self.created_threads)}
        elif "pullRequests/42/threads" in url:
            payload = {"value": self.threads}
        elif "pullRequests/42/commits" in url:
            payload = {"value": []}
        elif "pullRequests/42" in url:
            payload = PR_PAYLOAD
        elif "connectionData" in url:
            payload = {"authenticatedUser": {"id": "reviewer-1", "displayName": "Bot"}}
        elif "workItemsBatch" in url:
            payload = {"value": []}
        elif "workItems" in url:
            payload = {"value": []}
        else:  # pragma: no cover - defensive
            raise AssertionError(f"unexpected ADO URL: {method} {url}")
        return _FakeResponse(payload)


class _FakeResponse:
    def __init__(self, payload):
        self._body = json.dumps(payload).encode()

    def read(self):
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


@pytest.fixture
def ado(monkeypatch) -> AdoStub:
    stub = AdoStub()
    monkeypatch.setattr(
        "reviewforge.ado.client.urllib.request.urlopen", stub
    )
    return stub


class PiStub:
    """Canned Pi responses keyed by stage label."""

    def __init__(self, responder):
        self.calls: list[tuple[str, str]] = []
        self._responder = responder

    def run_json(self, prompt_path, stdin_text, output_path, stage):
        self.calls.append((stage, stdin_text))
        payload = self._responder(stage, stdin_text)
        Path(output_path).write_text(json.dumps(payload), encoding="utf-8")


def _install_pi(monkeypatch, responder) -> PiStub:
    stub = PiStub(responder)
    monkeypatch.setattr(
        "reviewforge.ai.runner.PiRunner.run_json", stub.run_json
    )
    return stub


def _review_result(findings: list[dict]) -> dict:
    return {
        "review_summary": {"summary": "reviewed"},
        "verification_summary": {"summary": "verified", "approach": "single pi"},
        "pr_summary": {"implementation_summary": "did things"},
        "findings": findings,
        "uncertainties": [],
    }


def _rich_finding(title: str, *, file=None, line=None, severity="major") -> dict:
    return {
        "title": title,
        "observation": f"observed {title}",
        "impact": f"impact of {title}",
        "recommendation": f"fix {title}",
        "severity": severity,
        "file": file,
        "line": line,
        "evidence": {"changedLines": [line or 1], "whyNewInThisPr": "introduced by this PR"},
    }


# ---------------------------------------------------------------------------
# Full-pipeline runs (fetch → git → reasoning → anchors → posting)
# ---------------------------------------------------------------------------


class TestFullRunEndToEnd:
    def test_single_chunk_run_posts_and_writes_artifacts(self, cfg, git_repo, ado, monkeypatch):
        findings = [
            _rich_finding("inline bug", file="src/app.py", line=3),
            _rich_finding("general note"),
            _rich_finding("ghost anchor", file="src/app.py", line=999),
        ]
        pi = _install_pi(monkeypatch, lambda stage, _stdin: _review_result(findings))

        outcome = orchestrator.run_full(cfg)

        assert outcome.exit_code == 0, [r.error for r in outcome.stages if r.status == "failed"]
        # One Pi call, containing the shared prefix and the real diff.
        assert len(pi.calls) == 1
        assert "Single-call reasoning review" in pi.calls[0][1]
        assert "CHANGED" in pi.calls[0][1]

        # Posting: inline finding gets a threadContext, the general note
        # does not, and the unmappable anchor is retained (no_line_mapping)
        # instead of being posted detached.
        assert len(ado.created_threads) == 2
        inline = next(t for t in ado.created_threads if "inline bug" in json.dumps(t))
        general = next(t for t in ado.created_threads if "general note" in json.dumps(t))
        assert inline["threadContext"]["filePath"] == "/src/app.py"
        assert "threadContext" not in general

        artifacts_dir = _artifacts_dir(cfg)
        posted = json.loads((artifacts_dir / "posted-findings.json").read_text(encoding="utf-8"))
        assert posted["created"] == 2
        assert posted["skipped_reasons"]["no_line_mapping"] == 1

        # Downgraded finding keeps its code anchor in the final doc.
        final = json.loads((artifacts_dir / "final-findings.json").read_text(encoding="utf-8"))
        ghost = next(f for f in final["findings"] if f["title"] == "ghost anchor")
        assert ghost["file"] == "src/app.py" and ghost["line"] == 999
        assert ghost["anchorDowngraded"] is True

        # Canonical artifacts exist and the summary records real counts.
        for name in ("final-findings.json", "run-summary.json", "run.log", "sarif-findings.json"):
            assert (artifacts_dir / name).exists(), name
        summary = json.loads((artifacts_dir / "run-summary.json").read_text(encoding="utf-8"))
        assert summary["finding_counts"]["final"] == 3

    def test_chunked_run_repeats_shared_prefix_without_session(self, cfg, git_repo, ado, monkeypatch):
        import dataclasses

        cfg = dataclasses.replace(cfg, max_diff_bytes=200, pi_session_enabled=False)
        finding = _rich_finding("dup finding", file="src/app.py", line=3)
        pi = _install_pi(
            monkeypatch,
            lambda stage, _stdin: {"findings": [finding], "uncertainties": []},
        )

        outcome = orchestrator.run_full(cfg)

        assert outcome.exit_code == 0, [r.error for r in outcome.stages if r.status == "failed"]
        chunk_calls = [c for c in pi.calls if c[0].startswith("single-pi chunk")]
        assert len(chunk_calls) > 1
        # Every chunk carries the shared context when no session persists it.
        for _stage, instruction in chunk_calls:
            assert "Single-call reasoning review" in instruction
            assert "src/app.py" in instruction
        # Identical findings across chunks dedupe to a single posted thread.
        assert len(ado.created_threads) == 1

    def test_rerun_skips_already_posted_findings(self, cfg, git_repo, ado, monkeypatch):
        findings = [_rich_finding("stable bug", file="src/app.py", line=3)]
        _install_pi(monkeypatch, lambda stage, _stdin: _review_result(findings))

        first = orchestrator.run_full(cfg)
        assert first.exit_code == 0
        assert len(ado.created_threads) == 1

        # Feed the posted thread back as existing state, then rerun.
        ado.threads = [
            {
                "id": 100 + index,
                "status": "active",
                "threadContext": body.get("threadContext"),
                "comments": [
                    {
                        "id": 1,
                        "author": {"id": "reviewer-1", "displayName": "Bot"},
                        "content": body["comments"][0]["content"],
                        "publishedDate": "2026-01-01T00:00:00Z",
                    }
                ],
            }
            for index, body in enumerate(ado.created_threads)
        ]
        import dataclasses

        cfg = dataclasses.replace(cfg, review_run_id="run-2")
        second = orchestrator.run_full(cfg)

        assert second.exit_code == 0
        assert len(ado.created_threads) == 1  # nothing new posted
        artifacts_dir = _artifacts_dir(cfg)
        posted = json.loads((artifacts_dir / "posted-findings.json").read_text(encoding="utf-8"))
        assert posted["created"] == 0
        assert posted["skipped_reasons"]["duplicate"] == 1


# ---------------------------------------------------------------------------
# Post-only runs (reviewforge post --input)
# ---------------------------------------------------------------------------


def _final_doc(findings: list[dict]) -> dict:
    return {"summary": "review summary", "findings": findings}


def _postable_finding(title: str, *, severity="major", file=None, line=None, downgraded=False) -> dict:
    finding = {
        "title": title,
        "message": f"message for {title}",
        "severity": severity,
        "suggestion": f"fix {title}",
        "confidence": "high",
        "evidence": {"changedLines": [1], "whyNewInThisPr": "new in this PR"},
        "file": file,
        "line": line,
    }
    if downgraded:
        finding["anchorDowngraded"] = True
    return finding


class TestPostOnlyEndToEnd:
    def test_fail_on_posts_then_fails_run(self, cfg, ado, monkeypatch, tmp_path):
        monkeypatch.setenv("FAIL_ON", "major")
        doc = tmp_path / "final.json"
        doc.write_text(
            json.dumps(_final_doc([_postable_finding("blocker bug", severity="blocker")])),
            encoding="utf-8",
        )

        outcome = orchestrator.run_post_only(cfg, input_path=doc)

        assert outcome.exit_code != 0
        post_stage = next(r for r in outcome.stages if r.name == "post_to_ado")
        assert post_stage.status == "failed"
        # Findings were still posted before the failure was reported.
        assert len(ado.created_threads) == 1
        artifacts_dir = _artifacts_dir(cfg)
        posted = json.loads((artifacts_dir / "posted-findings.json").read_text(encoding="utf-8"))
        assert posted["failOnTriggered"] is True
        assert posted["created"] == 1

    def test_downgraded_anchor_retained_fileless_posted(self, cfg, ado, tmp_path):
        doc = tmp_path / "final.json"
        doc.write_text(
            json.dumps(
                _final_doc(
                    [
                        _postable_finding("lost anchor", file="src/app.py", line=99, downgraded=True),
                        _postable_finding("process note"),
                    ]
                )
            ),
            encoding="utf-8",
        )

        outcome = orchestrator.run_post_only(cfg, input_path=doc)

        assert outcome.exit_code == 0, [r.error for r in outcome.stages if r.status == "failed"]
        assert len(ado.created_threads) == 1
        assert "process note" in json.dumps(ado.created_threads[0])
        assert "threadContext" not in ado.created_threads[0]
        artifacts_dir = _artifacts_dir(cfg)
        posted = json.loads((artifacts_dir / "posted-findings.json").read_text(encoding="utf-8"))
        assert posted["skipped_reasons"]["no_line_mapping"] == 1

    def test_validation_error_surfaces_detail(self, cfg, ado, tmp_path, monkeypatch):
        monkeypatch.setenv("POST_MIN_SEVERITY", "catastrophic")
        doc = tmp_path / "final.json"
        doc.write_text(
            json.dumps(_final_doc([_postable_finding("real finding")])),
            encoding="utf-8",
        )

        outcome = orchestrator.run_post_only(cfg, input_path=doc)

        assert outcome.exit_code != 0
        post_stage = next(r for r in outcome.stages if r.name == "post_to_ado")
        # The helper's actionable validation message survives the
        # in-process boundary instead of becoming a generic failure.
        assert "POST_MIN_SEVERITY" in (post_stage.error or "")
        assert not ado.created_threads
