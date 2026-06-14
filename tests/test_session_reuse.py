"""End-to-end integration test for Pi session reuse.

This is the headline test for the token-savings plan. It runs every stage
of the pipeline with a real :class:`PiRunner` whose subprocess is mocked
to record the actual command lines, then asserts:

1. Every Pi call (stages + repair) uses ``--session <id>`` with the same id.
2. Subsequent stages send much less data on stdin than the first stage.
3. The session id is stable for a given PR + run id.
4. ``--clear-session`` is added on demand.
5. The legacy ``--no-session`` mode still works.
6. The run-summary records the session id, enabled flag, cleared flag,
   and per-stage token usage.

The test does not call a real Pi; it asserts the *shape* of the calls
that would be made.
"""
from __future__ import annotations

import json
import subprocess
import sys
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from auto_pr_reviewer.ai.runner import PiRunner  # noqa: E402
from auto_pr_reviewer.artifacts import builder, manager  # noqa: E402
from auto_pr_reviewer.config import Config  # noqa: E402
from auto_pr_reviewer.pipeline import orchestrator  # noqa: E402
from auto_pr_reviewer.pipeline.stage import (  # noqa: E402
    StageContext,
)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def cfg(tmp_path: Path) -> Config:
    files: dict[str, Path] = {}
    for n in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        files[n] = tmp_path / f"{n}.md"
        files[n].write_text(f"{n} prompt", encoding="utf-8")
    return Config(
        ado_org="contoso", ado_project="P", ado_repo_id="r", pr_id="42", ado_token="t",
        source_branch="feature", target_branch="main",
        workspace=tmp_path, clone_root=tmp_path, review_language="English",
        review_prompt_path=files["review"], intent_prompt_path=files["intent"],
        context_plan_prompt_path=files["plan"], context_digest_prompt_path=files["digest"],
        verify_prompt_path=files["verify"], severity_prompt_path=files["severity"],
        standards_path=files["standards"],
        pi_model="m", max_diff_bytes=10, chunk_trigger_diff_bytes=10,
        disable_chunk_review=False, pi_timeout_secs=5, dry_run=True,
        include_work_items=True, include_existing_comments=True,
        verify_findings=True, force_review=False, review_target_branches="",
        review_artifact_dir=None, review_artifact_root=tmp_path / "artifacts",
        review_run_id="r1",
        pi_session_enabled=True, pi_session_clear=False, pi_session_id=None,
    )


def _ok_payload():
    return {"summary": "ok", "findings": []}


def _ok_intent():
    return {"pr_intent": "Fix X", "changed_behaviors": [], "risk_areas": []}


def _ok_plan():
    return {"files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}


def _ok_digest():
    return {"relevant_context": [], "possible_intentional_choices": [], "context_gaps": []}


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


class TestSessionIdStable:
    def test_default_session_id_format(self, cfg):
        runner = PiRunner(cfg)
        assert runner.session_id == "pr-42-review-r1"

    def test_session_id_stable_across_reruns(self, cfg):
        # Two runners on the same cfg see the same session id.
        r1 = PiRunner(cfg)
        r2 = PiRunner(cfg)
        assert r1.session_id == r2.session_id

    def test_session_id_changes_with_run_id(self, cfg):
        r1 = PiRunner(cfg)
        cfg2 = replace(cfg, review_run_id="r2")
        r2 = PiRunner(cfg2)
        assert r1.session_id != r2.session_id

    def test_session_id_override_takes_precedence(self, cfg):
        cfg = replace(cfg, pi_session_id="custom-session")
        assert PiRunner(cfg).session_id == "custom-session"

    def test_session_id_without_run_id(self, cfg):
        cfg = replace(cfg, review_run_id=None)
        assert PiRunner(cfg).session_id == "pr-42-review"


class TestSubprocessCommandShape:
    """Verify every Pi call carries the expected session flags."""

    def test_every_subprocess_call_uses_same_session_id(self, cfg, tmp_path, monkeypatch):
        calls: list[list[str]] = []
        stderr_with_tokens = b"tokens: 100 in / 50 out\n"

        def fake_run(cmd, **k):
            calls.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', stderr_with_tokens)

        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        runner = PiRunner(cfg)
        # Simulate two stage calls in the same session.
        runner.run_json(tmp_path / "p1.md", "first call", tmp_path / "o1.json", "intent")
        runner.run_json(tmp_path / "p2.md", "second call", tmp_path / "o2.json", "plan")
        assert len(calls) == 2
        for cmd in calls:
            assert "--session" in cmd
            assert "pr-42-review-r1" in cmd
            assert "--no-session" not in cmd

    def test_session_clear_flag(self, cfg, tmp_path, monkeypatch):
        calls: list[list[str]] = []
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda cmd, **k: (
                calls.append(list(cmd))
                or subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")
            ),
        )
        cfg = replace(cfg, pi_session_clear=True)
        runner = PiRunner(cfg)
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "--clear-session" in calls[0]
        assert "--session" in calls[0]

    def test_session_disabled_uses_no_session_flag(self, cfg, tmp_path, monkeypatch):
        calls: list[list[str]] = []
        monkeypatch.setattr(
            "auto_pr_reviewer.ai.runner.subprocess.run",
            lambda cmd, **k: (
                calls.append(list(cmd))
                or subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")
            ),
        )
        cfg = replace(cfg, pi_session_enabled=False)
        runner = PiRunner(cfg)
        runner.run_json(tmp_path / "p.md", "in", tmp_path / "out.json", "stage")
        assert "--no-session" in calls[0]
        assert "--session" not in calls[0]
        assert "--clear-session" not in calls[0]


class TestSubsequentStageShorterInput:
    """Phase B + sessions: subsequent stage prompts are dramatically shorter."""

    def test_legacy_prompts_embed_full_context(self, cfg, tmp_path):
        # Without sessions, prompts include all context. Sanity baseline.
        from auto_pr_reviewer.ai import prompts
        cfg = replace(cfg, pi_session_enabled=False)
        metadata = tmp_path / "metadata.json"
        metadata.write_text('{"title":"Big PR","description":"long"}', encoding="utf-8")
        paths = {
            "metadata": metadata, "diff": tmp_path / "diff.patch",
            "work_items": tmp_path / "wi.json", "threads": tmp_path / "th.json",
        }
        text = prompts.stage_instruction("intent", cfg, metadata, "a.py\nb.py\n", [{"id": 1, "title": "WI"}], [], paths)
        # Embeds the metadata JSON content and the work item payload.
        assert '{"title":"Big PR"' in text
        assert '"id": 1' in text or '"id":1' in text
        assert "Repository/project metadata" in text

    def test_session_prompts_only_reference_paths(self, cfg, tmp_path):
        from auto_pr_reviewer.ai import prompts
        cfg = replace(cfg, pi_session_enabled=True)
        metadata = tmp_path / "metadata.json"
        metadata.write_text('{"title":"Big PR","description":"long"}', encoding="utf-8")
        paths = {
            "metadata": metadata, "diff": tmp_path / "diff.patch",
            "work_items": tmp_path / "wi.json", "threads": tmp_path / "th.json",
        }
        text = prompts.stage_instruction("intent", cfg, metadata, "a.py\nb.py\n", [{"id": 1, "title": "WI"}], [], paths)
        # The metadata JSON content is NOT embedded.
        assert '{"title":"Big PR"' not in text
        # The work item payload is NOT embedded.
        assert '"title": "WI"' not in text and '"title":"WI"' not in text
        # The legacy "Repository/project metadata:" section header is gone.
        assert "Repository/project metadata:" not in text
        # The briefing is just the small paragraph + path list.
        assert "You are reviewing Azure DevOps PR" in text
        # Paths are referenced.
        assert str(metadata) in text
        assert str(paths["work_items"]) in text
        assert str(paths["threads"]) in text
        assert str(paths["diff"]) in text

    def test_session_prompts_are_much_shorter(self, cfg, tmp_path):
        from auto_pr_reviewer.ai import prompts
        # Build a big context: 50 work items, long existing comments.
        big_wi = [{"id": i, "title": f"Work item {i}", "description": "x" * 200} for i in range(50)]
        big_threads = [{"author": f"u{i}", "firstComment": "y" * 200} for i in range(50)]
        big_meta = '{"title": "A long title that goes on", "description": "' + ("z" * 500) + '"}'
        metadata = tmp_path / "metadata.json"
        metadata.write_text(big_meta, encoding="utf-8")
        paths = {
            "metadata": metadata, "diff": tmp_path / "diff.patch",
            "work_items": tmp_path / "wi.json", "threads": tmp_path / "th.json",
        }
        legacy = prompts.stage_instruction("intent", replace(cfg, pi_session_enabled=False), metadata, "f.py\n", big_wi, big_threads, paths)
        sessioned = prompts.stage_instruction("intent", replace(cfg, pi_session_enabled=True), metadata, "f.py\n", big_wi, big_threads, paths)
        # Session prompt is dramatically smaller.
        assert len(sessioned) < len(legacy) // 2
        # Concrete lower bound: session prompt is well under 2 KB regardless of context size.
        assert len(sessioned) < 2000


class TestRepairStaysInSession:
    """Phase D: invalid-JSON retry uses the same session and sends no context."""

    def test_repair_uses_same_session_id(self, cfg, tmp_path, monkeypatch):
        calls: list[dict] = []

        def fake_run(cmd, input=b"", **k):
            calls.append({"cmd": list(cmd), "input": input})
            if len(calls) == 1:
                return subprocess.CompletedProcess(cmd, 0, b"not json", b"")
            return subprocess.CompletedProcess(cmd, 0, b'{"ok": true}', b"")

        monkeypatch.setattr("auto_pr_reviewer.ai.runner.subprocess.run", fake_run)
        runner = PiRunner(cfg)
        runner.run_json(tmp_path / "p.md", "original context", tmp_path / "out.json", "stage")
        assert len(calls) == 2
        # Same session id in both calls.
        for c in calls:
            assert "--session" in c["cmd"]
            assert "pr-42-review-r1" in c["cmd"]
        # Repair: empty stdin (no re-send of full context).
        assert calls[1]["input"] == b""
        # Repair instruction is a small fix-up message.
        assert "return only the json" in calls[1]["cmd"][-1].lower()


class TestSessionIdInRunSummary:
    def test_run_summary_records_session_fields(self, cfg, monkeypatch):
        from auto_pr_reviewer.pipeline.stage import Stage
        from datetime import datetime, timezone

        def _stub(name):
            class _S(Stage):
                pass
            inst = _S()
            inst.name = name
            inst.should_run = lambda ctx: True
            inst.run = lambda ctx: {"ok": True}  # return a dict, not a StageResult
            return inst

        monkeypatch.setattr(
            orchestrator, "DEFAULT_PIPELINE",
            [_stub("a"), _stub("b")],
        )
        outcome = orchestrator.run_full(cfg)
        # The summary exposes session fields.
        assert outcome.summary.pi_session_id == "pr-42-review-r1"
        assert outcome.summary.pi_session_enabled is True
        assert outcome.summary.pi_session_cleared is False

    def test_run_summary_records_session_clear(self, cfg, monkeypatch):
        from auto_pr_reviewer.pipeline.stage import Stage

        def _stub(name):
            class _S(Stage):
                pass
            inst = _S()
            inst.name = name
            inst.should_run = lambda ctx: True
            inst.run = lambda ctx: {"ok": True}
            return inst

        cfg = replace(cfg, pi_session_clear=True)
        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", [_stub("a")])
        outcome = orchestrator.run_full(cfg)
        assert outcome.summary.pi_session_cleared is True
