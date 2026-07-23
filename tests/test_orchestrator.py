"""Tests for the top-level orchestrator entry points.

Covers :func:`ensure_tools`, :func:`should_skip`, and the three
:func:`run_*` entry points. Stages are replaced with recording stubs so
the orchestrator's bookkeeping (run summary, exit code, artifact
persistence) is exercised end-to-end without involving Pi.
"""
from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "src"))

from reviewforge.artifacts import builder, manager  # noqa: E402
from reviewforge.config import Config  # noqa: E402
from reviewforge.exceptions import ReviewForgeError  # noqa: E402
from reviewforge.pipeline import orchestrator  # noqa: E402
from reviewforge.pipeline.orchestrator import (  # noqa: E402
    ensure_tools,
    run_full,
    run_post_only,
    run_review_only,
    should_skip,
)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture
def cfg(tmp_path: Path) -> Config:
    files: dict[str, Path] = {}
    for name in ["review", "intent", "plan", "digest", "verify", "severity", "standards"]:
        files[name] = tmp_path / f"{name}.md"
        files[name].write_text(f"{name}", encoding="utf-8")
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
        standards_path=files["standards"],
        pi_model="m",
        max_diff_bytes=10,
        chunk_trigger_diff_bytes=10,
        disable_chunk_review=False,
        pi_timeout_secs=5,
        dry_run=True,
        include_work_items=True,
        include_existing_comments=True,
        verify_findings=True,
        force_review=False,
        review_target_branches="",
        review_artifact_dir=None,
        review_artifact_root=tmp_path / "artifacts",
        review_run_id="run-1",
    )


# ---------------------------------------------------------------------------
# ensure_tools
# ---------------------------------------------------------------------------


class TestEnsureTools:
    def test_raises_when_tool_missing(self, monkeypatch):
        monkeypatch.setattr(
            "reviewforge.pipeline.orchestrator.shutil.which", lambda t: None
        )
        with pytest.raises(ReviewForgeError):
            ensure_tools()

    def test_returns_when_all_tools_present(self, monkeypatch):
        monkeypatch.setattr(
            "reviewforge.pipeline.orchestrator.shutil.which", lambda t: "/usr/bin/" + t
        )
        ensure_tools()  # no raise


# ---------------------------------------------------------------------------
# should_skip
# ---------------------------------------------------------------------------


class TestShouldSkip:
    def test_force_review_bypasses_skip(self, cfg):
        cfg = dataclass_replace(cfg, force_review=True)
        assert should_skip(cfg, {"isDraft": True}) is None

    def test_skips_drafts(self, cfg):
        reason = should_skip(cfg, {"isDraft": True, "status": "active"})
        assert reason == {"summary": "Skipped: PR is draft.", "findings": []}

    def test_skips_closed(self, cfg):
        reason = should_skip(cfg, {"isDraft": False, "status": "closed"})
        assert "closed" in reason["summary"]

    def test_skips_completed(self, cfg):
        reason = should_skip(cfg, {"isDraft": False, "status": "completed"})
        assert "completed" in reason["summary"]

    def test_skips_target_branch_not_allowed(self, cfg):
        cfg = dataclass_replace(cfg, review_target_branches="refs/heads/release")
        reason = should_skip(cfg, {"isDraft": False, "status": "active", "targetRefName": "refs/heads/main"})
        assert "not in review policy" in reason["summary"]

    def test_allows_target_branch_in_policy(self, cfg):
        cfg = dataclass_replace(cfg, review_target_branches="main,develop")
        assert should_skip(cfg, {"isDraft": False, "status": "active", "targetRefName": "refs/heads/main"}) is None

    def test_allows_missing_target_ref(self, cfg):
        cfg = dataclass_replace(cfg, review_target_branches="main")
        # No target ref present, so the policy does not trigger.
        assert should_skip(cfg, {"isDraft": False, "status": "active"}) is None

    def test_returns_none_for_active_open_pr(self, cfg):
        assert should_skip(cfg, {"isDraft": False, "status": "active"}) is None


# ---------------------------------------------------------------------------
# Stage stub helpers
# ---------------------------------------------------------------------------


def _make_stub(name, next_status="ok", details=None):
    """Build a Stage subclass on the fly with the desired behavior."""
    from reviewforge.pipeline.stage import Stage

    class _Stub(Stage):
        pass

    inst = _Stub()
    inst.name = name
    inst._next_status = next_status
    inst._details = details or {}

    def _should_run(ctx):
        return next_status != "skipped"

    def _run(ctx):
        ctx.extras.setdefault("stage_calls", []).append(name)
        if next_status == "failed":
            raise SystemExit(f"forced failure in {name}")
        return inst._details

    inst.should_run = _should_run
    inst.run = _run
    return inst


def dataclass_replace(cfg, **kw):
    """Helper that re-imports dataclasses.replace; ensures availability in tests."""
    from dataclasses import replace
    return replace(cfg, **kw)


# ---------------------------------------------------------------------------
# run_full
# ---------------------------------------------------------------------------


class TestRunFull:
    def test_records_stage_results_in_summary(self, cfg, monkeypatch):
        # Replace the default pipeline with two trivial stubs.
        stubs = [_make_stub("a"), _make_stub("b")]
        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", stubs)
        outcome = run_full(cfg)
        assert outcome.exit_code == 0
        names = [r.name for r in outcome.stages]
        assert names == ["a", "b"]
        summary = builder.read_json(manager.create(cfg).summary)  # sanity: file present after create
        # The actual file was written to the artifacts dir of the outcome.
        summary_path = next(r for r in outcome.stages if r.name == "a")
        # The run-summary.json is written by the orchestrator.
        assert outcome.summary.exit_code == 0
        assert outcome.summary.dry_run is True

    def test_failure_propagates_exit_code(self, cfg, monkeypatch):
        stubs = [_make_stub("a"), _make_stub("b", next_status="failed"), _make_stub("c")]
        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", stubs)
        outcome = run_full(cfg)
        assert outcome.exit_code == 1
        # Runner stops at first failure: only "a" and "b" should appear.
        assert [r.name for r in outcome.stages] == ["a", "b"]
        assert outcome.stages[-1].status == "failed"

    def test_writes_run_summary_to_artifact_dir(self, cfg, monkeypatch):
        stubs = [_make_stub("a", details={"k": 1})]
        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", stubs)
        outcome = run_full(cfg)
        # Locate the summary file via the run id we set in the config.
        run_id = cfg.review_run_id
        path = cfg.review_artifact_root / f"pr-{cfg.pr_id}" / "runs" / run_id / "run-summary.json"
        assert path.exists()
        payload = json.loads(path.read_text())
        assert payload["pr_id"] == "42"
        assert payload["dry_run"] is True
        assert payload["exit_code"] == 0
        assert payload["stages"][0]["name"] == "a"
        assert payload["stages"][0]["details"] == {"k": 1}
        assert payload["duration_ms"] >= 0

    def test_persists_chronological_stage_and_pi_log(self, cfg, monkeypatch, capsys):
        from reviewforge.runlog import info

        class PiStage:
            name = "pi"

            def __call__(self, ctx):
                from reviewforge.pipeline.stage import StageResult, StageStatus

                info("[pi review] streamed stderr")
                return StageResult(
                    name=self.name,
                    status=StageStatus.OK,
                    started_at="",
                    finished_at="",
                    duration_ms=0,
                )

        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", [PiStage()])
        run_full(cfg)
        log_path = cfg.review_artifact_root / "pr-42" / "runs" / "run-1" / "run.log"
        log = log_path.read_text(encoding="utf-8")
        assert log.index("stage pi started") < log.index("[pi review] streamed stderr") < log.index("stage pi finished")
        assert "[pi review] streamed stderr" in capsys.readouterr().err

# ---------------------------------------------------------------------------
# run_review_only
# ---------------------------------------------------------------------------


class TestRunReviewOnly:
    def test_uses_review_only_pipeline(self, cfg, monkeypatch):
        from reviewforge.pipeline import stages
        recorded = []

        def fake_run(stages_list, ctx):
            recorded.extend(s.name for s in stages_list)
            return []

        monkeypatch.setattr(orchestrator, "run_stages", fake_run)
        monkeypatch.setattr(orchestrator, "REVIEW_ONLY_PIPELINE", [_make_stub("x")])
        outcome = run_review_only(cfg, output=None)
        assert outcome.exit_code == 0
        assert recorded == ["x"]
        assert outcome.summary.posted.get("review_only") == 1

    def test_copies_final_to_output(self, cfg, monkeypatch, tmp_path):
        # Populate artifacts.final with a known doc.
        artifacts = manager.create(cfg)
        builder.write_json(artifacts.final, {"summary": "ok", "findings": []})
        out = tmp_path / "out.json"

        # Reuse the real REVIEW_ONLY_PIPELINE: skip the heavy work.
        monkeypatch.setattr(orchestrator, "REVIEW_ONLY_PIPELINE", [_make_stub("a")])
        # Make run_stages a no-op that preserves the on-disk final.
        monkeypatch.setattr(orchestrator, "run_stages", lambda *a, **k: [])

        run_review_only(cfg, output=out)
        assert out.exists()
        assert json.loads(out.read_text()) == {"summary": "ok", "findings": []}

    def test_failure_propagates(self, cfg, monkeypatch):
        monkeypatch.setattr(
            orchestrator, "REVIEW_ONLY_PIPELINE", [_make_stub("a", next_status="failed")]
        )
        outcome = run_review_only(cfg)
        assert outcome.exit_code == 1


# ---------------------------------------------------------------------------
# run_post_only
# ---------------------------------------------------------------------------


class TestRunPostOnly:
    def test_raises_when_input_missing(self, cfg, tmp_path):
        with pytest.raises(ReviewForgeError):
            run_post_only(cfg, input_path=tmp_path / "missing.json")

    def test_posts_input_without_fragment_artifacts(self, cfg, tmp_path, monkeypatch):
        payload = {
            "summary": "ok",
            "findings": [{
                "severity": "major", "title": "T", "message": "M", "suggestion": "Fix it.",
                "evidence": {"changedLines": [1], "whyNewInThisPr": "Introduced by the change."},
            }],
        }
        input_path = tmp_path / "review.json"
        input_path.write_text(json.dumps(payload), encoding="utf-8")
        recorded = []

        def fake_run(stages_list, ctx):
            assert ctx.final == payload
            assert not ctx.artifacts.severity.exists()
            assert not ctx.artifacts.final.exists()
            recorded.extend(s.name for s in stages_list)
            return []

        monkeypatch.setattr(orchestrator, "run_stages", fake_run)
        monkeypatch.setattr(orchestrator, "POST_ONLY_PIPELINE", [_make_stub("p1"), _make_stub("p2")])
        outcome = run_post_only(cfg, input_path=input_path)
        assert outcome.exit_code == 0
        assert recorded == ["p1", "p2"]

    def test_invalid_input_raises(self, cfg, tmp_path):
        # Non-conforming doc: severity is not a string. validate_review_doc
        # raises SystemExit.
        input_path = tmp_path / "review.json"
        input_path.write_text(json.dumps({"summary": 123, "findings": []}), encoding="utf-8")
        with pytest.raises(ReviewForgeError):
            run_post_only(cfg, input_path=input_path)

    def test_failure_propagates(self, cfg, tmp_path, monkeypatch):
        input_path = tmp_path / "review.json"
        input_path.write_text(json.dumps({"summary": "ok", "findings": []}), encoding="utf-8")
        monkeypatch.setattr(
            orchestrator, "POST_ONLY_PIPELINE", [_make_stub("p", next_status="failed")]
        )
        outcome = run_post_only(cfg, input_path=input_path)
        assert outcome.exit_code == 1


# ---------------------------------------------------------------------------
# Run summary / record helpers
# ---------------------------------------------------------------------------


class TestRunOutcome:
    def test_success_property(self, cfg, monkeypatch):
        monkeypatch.setattr(orchestrator, "DEFAULT_PIPELINE", [_make_stub("a")])
        outcome = run_full(cfg)
        assert outcome.success is True

    def test_failure_success_property(self, cfg, monkeypatch):
        monkeypatch.setattr(
            orchestrator, "DEFAULT_PIPELINE", [_make_stub("a", next_status="failed")]
        )
        outcome = run_full(cfg)
        assert outcome.success is False
