"""Tests for the fast review mode (single Pi call)."""
from __future__ import annotations

import json
import sys
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import MagicMock

import pytest

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src"
sys.path.insert(0, str(SRC))

from reviewforge.artifacts import builder, manager  # noqa: E402
from reviewforge.config import Config  # noqa: E402
from reviewforge.pipeline.schemas import FastReviewResult  # noqa: E402
from reviewforge.pipeline.stage import StageContext, StageStatus  # noqa: E402
from reviewforge.pipeline.stages import (  # noqa: E402
    FAST_REVIEW_PIPELINE,
    FAST_REVIEW_REVIEW_ONLY_PIPELINE,
    FastReviewStage,
)
from reviewforge.pipeline.stages.fast_review import (  # noqa: E402
    _build_fast_review_instruction,
    _normalize_finding,
)
from reviewforge.pipeline import orchestrator  # noqa: E402


@pytest.fixture
def cfg(tmp_path: Path) -> Config:
    files: dict[str, Path] = {}
    for name in [
        "review",
        "intent",
        "plan",
        "digest",
        "verify",
        "severity",
        "standards",
        "fast-review",
    ]:
        p = tmp_path / f"{name}.md"
        p.write_text(f"{name} prompt", encoding="utf-8")
        files[name] = p
    return Config(
        ado_org="contoso",
        ado_project="Payments",
        ado_repo_id="api",
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
        fast_review_prompt_path=files["fast-review"],
        pi_model="test/model",
        max_diff_bytes=100,
        chunk_trigger_diff_bytes=100,
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
        fast_review=True,
    )


@pytest.fixture
def artifacts(cfg: Config):
    return manager.create(cfg)


def _make_state(diff_text: str = "diff", files: list[str] | None = None):
    return SimpleNamespace(
        diff_text=diff_text,
        files=files or ["a.py"],
        target_branch="main",
        source_branch="feature",
        target_commit="abc",
        source_commit="def",
    )


def _make_pi(payload: dict[str, Any]) -> MagicMock:
    """Build a mocked PiRunner that writes ``payload`` to the requested path."""
    pi = MagicMock()

    def record(prompt, stdin, out, stage):
        builder.write_json(out, payload)

    pi.run_json.side_effect = record
    pi.last_tokens = {"in": 100, "out": 50, "total": 150}
    return pi


def _stage_context(cfg, artifacts, pi, diff_text: str = "diff"):
    builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=_make_state(diff_text), pi=pi)
    ctx.files_text = "a.py\n"
    ctx.extras["wi_context"] = []
    ctx.extras["thread_context"] = []
    return ctx


def _valid_payload() -> dict[str, Any]:
    return {
        "intent": {
            "pr_intent": "Add a new helper.",
            "changed_behaviors": ["Helper is available."],
            "risk_areas": [],
        },
        "context_summary": {
            "files_read": [{"path": "a.py", "reason": "context"}],
            "searches_run": [],
            "tests_inspected": ["tests/test_a.py"],
            "notes": "Read a.py.",
        },
        "review_summary": {"summary": "Clean change.", "notes": ""},
        "verification_summary": {"summary": "Verified.", "notes": ""},
        "findings": [
            {
                "severity": "major",
                "title": "Missing input validation",
                "message": "The helper does not validate input.",
                "file": "a.py",
                "line": 10,
                "confidence": "high",
                "contextBasis": "surrounding-code-read",
                "suggestion": "Add validation.",
                "evidence": {
                    "changedLines": [10],
                    "contextFilesRead": ["a.py"],
                    "whyNewInThisPr": "Introduced in this PR.",
                    "whyNotIntentional": "No guard elsewhere.",
                },
            }
        ],
        "statistics": {
            "findings_count": 1,
            "by_severity": {"blocker": 0, "major": 1, "minor": 0, "nit": 0},
            "files_read_count": 1,
            "searches_run_count": 0,
            "tests_inspected_count": 1,
        },
    }


class TestFastReviewResultSchema:
    def test_valid_payload_round_trips(self):
        raw = _valid_payload()
        result = FastReviewResult.model_validate(raw)
        assert result.intent.pr_intent == "Add a new helper."
        assert len(result.findings) == 1
        assert result.findings[0].severity == "major"
        assert result.findings[0].evidence.changedLines == [10]

    def test_missing_required_summary_fails(self):
        raw = _valid_payload()
        raw["review_summary"] = {"summary": "", "notes": ""}
        with pytest.raises(Exception):
            FastReviewResult.model_validate(raw)

    def test_invalid_severity_fails(self):
        raw = _valid_payload()
        raw["findings"][0]["severity"] = "critical"
        with pytest.raises(Exception):
            FastReviewResult.model_validate(raw)


class TestBuildFastReviewInstruction:
    def test_includes_metadata_changed_files_and_diff(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi, diff_text="@@ -1 +1 @@\n-old\n+new")
        text = _build_fast_review_instruction(ctx)
        assert "Fast review mode for Azure DevOps PR #42" in text
        assert "active" in text
        assert "a.py" in text
        assert "@@ -1 +1 @@" in text

    def test_includes_work_items_and_threads(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi)
        ctx.extras["wi_context"] = [{"id": 1, "title": "wi"}]
        ctx.extras["thread_context"] = [{"id": 2, "comments": ["c"]}]
        text = _build_fast_review_instruction(ctx)
        assert '"id": 1' in text
        assert '"id": 2' in text

    def test_falls_back_to_artifact_diff(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi, diff_text="")
        artifacts.diff.write_text("fallback diff", encoding="utf-8")
        text = _build_fast_review_instruction(ctx)
        assert "fallback diff" in text

    def test_empty_output_raises_systemexit(self, cfg, artifacts):
        pi = MagicMock()

        def write_empty(prompt, stdin, out, stage):
            out.write_text("", encoding="utf-8")

        pi.run_json.side_effect = write_empty
        pi.last_tokens = {}
        ctx = _stage_context(cfg, artifacts, pi)
        result = FastReviewStage()(ctx)
        assert result.status == StageStatus.FAILED

class TestNormalizeFinding:
    def test_strips_leading_slash(self):
        f = {"file": "/src/a.py"}
        assert _normalize_finding(f)["file"] == "src/a.py"

    def test_leaves_none_file(self):
        f = {"file": None}
        assert _normalize_finding(f)["file"] is None


class TestFastReviewStage:
    def test_writes_all_synthesized_artifacts(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi)
        result = FastReviewStage()(ctx)

        assert result.status == StageStatus.OK
        assert result.details["findings"] == 1
        assert pi.run_json.call_count == 1

        assert artifacts.intent.exists()
        assert artifacts.plan.exists()
        assert artifacts.collected.exists()
        assert artifacts.digest.exists()
        assert artifacts.candidate.exists()
        assert artifacts.verified.exists()
        assert artifacts.severity.exists()
        assert artifacts.final.exists()

        intent = builder.read_json(artifacts.intent)
        assert intent["pr_intent"] == "Add a new helper."

        plan = builder.read_json(artifacts.plan)
        assert plan["files_to_read"][0]["path"] == "a.py"
        assert plan["tests_to_inspect"] == ["tests/test_a.py"]

        final = builder.read_json(artifacts.final)
        assert final["summary"] == "Clean change. Verified."
        assert len(final["findings"]) == 1
        assert final["findings"][0]["file"] == "a.py"

    def test_empty_findings_produces_valid_final(self, cfg, artifacts):
        payload = _valid_payload()
        payload["findings"] = []
        payload["statistics"]["findings_count"] = 0
        payload["statistics"]["by_severity"] = {
            "blocker": 0,
            "major": 0,
            "minor": 0,
            "nit": 0,
        }
        pi = _make_pi(payload)
        ctx = _stage_context(cfg, artifacts, pi)
        result = FastReviewStage()(ctx)
        assert result.status == StageStatus.OK
        final = builder.read_json(artifacts.final)
        assert final["findings"] == []

    def test_invalid_json_fails(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        pi.run_json.side_effect = lambda p, s, o, st: builder.write_json(o, {"not": "valid"})
        ctx = _stage_context(cfg, artifacts, pi)
        result = FastReviewStage()(ctx)
        assert result.status == StageStatus.FAILED

    def test_records_token_usage(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi)
        FastReviewStage()(ctx)
        assert ctx.last_token_usage == {"in": 100, "out": 50, "total": 150}


class TestPipelines:
    def test_fast_review_pipeline_has_expected_stages(self):
        names = [s.name for s in FAST_REVIEW_PIPELINE]
        assert names == [
            "fetch_pr_metadata",
            "prepare_repository",
            "build_artifacts",
            "fast_review",
            "ac_coverage",
            "post_to_ado",
        ]

    def test_fast_review_review_only_pipeline_has_expected_stages(self):
        names = [s.name for s in FAST_REVIEW_REVIEW_ONLY_PIPELINE]
        assert names == [
            "fetch_pr_metadata",
            "prepare_repository",
            "build_artifacts",
            "fast_review",
            "ac_coverage",
        ]


class TestOrchestratorBranching:
    def test_run_full_uses_fast_pipeline_when_fast_review_enabled(self, cfg, monkeypatch):
        called_with = []

        def capture(stages, ctx):
            called_with.append([s.name for s in stages])
            return []

        monkeypatch.setattr(orchestrator, "run_stages", capture)
        monkeypatch.setattr(orchestrator, "create_artifacts", lambda c: manager.create(c))
        monkeypatch.setattr(orchestrator, "PiRunner", lambda c: MagicMock())
        monkeypatch.setattr(orchestrator, "new_run_summary", lambda c, a: MagicMock())
        monkeypatch.setattr(orchestrator, "finalize_run_summary", lambda *a, **k: {})
        monkeypatch.setattr(builder, "write_json", lambda p, d: None)

        orchestrator.run_full(cfg)
        assert called_with[0] == [
            "fetch_pr_metadata",
            "prepare_repository",
            "build_artifacts",
            "fast_review",
            "ac_coverage",
            "post_to_ado",
        ]

    def test_run_review_only_uses_fast_review_only_pipeline(self, cfg, monkeypatch):
        called_with = []

        def capture(stages, ctx):
            called_with.append([s.name for s in stages])
            return []

        monkeypatch.setattr(orchestrator, "run_stages", capture)
        monkeypatch.setattr(orchestrator, "create_artifacts", lambda c: manager.create(c))
        monkeypatch.setattr(orchestrator, "PiRunner", lambda c: MagicMock())
        monkeypatch.setattr(orchestrator, "new_run_summary", lambda c, a: MagicMock())
        monkeypatch.setattr(orchestrator, "finalize_run_summary", lambda *a, **k: {})
        monkeypatch.setattr(builder, "write_json", lambda p, d: None)

        orchestrator.run_review_only(cfg)
        assert called_with[0] == [
            "fetch_pr_metadata",
            "prepare_repository",
            "build_artifacts",
            "fast_review",
            "ac_coverage",
        ]

    def test_run_full_uses_default_pipeline_when_fast_review_disabled(self, cfg, monkeypatch):
        cfg = replace(cfg, fast_review=False)
        called_with = []

        def capture(stages, ctx):
            called_with.append([s.name for s in stages])
            return []

        monkeypatch.setattr(orchestrator, "run_stages", capture)
        monkeypatch.setattr(orchestrator, "create_artifacts", lambda c: manager.create(c))
        monkeypatch.setattr(orchestrator, "PiRunner", lambda c: MagicMock())
        monkeypatch.setattr(orchestrator, "new_run_summary", lambda c, a: MagicMock())
        monkeypatch.setattr(orchestrator, "finalize_run_summary", lambda *a, **k: {})
        monkeypatch.setattr(builder, "write_json", lambda p, d: None)

        orchestrator.run_full(cfg)
        assert called_with[0] == [
            "fetch_pr_metadata",
            "prepare_repository",
            "build_artifacts",
            "reconstruct_intent",
            "plan_context",
            "collect_context",
            "context_digest",
            "review_diff",
            "verify_findings",
            "calibrate_severity",
            "ac_coverage",
            "post_to_ado",
        ]


class TestAcCoverageIntegration:
    def test_appends_uncovered_ac_finding_in_fast_mode(self, cfg, artifacts):
        pi = _make_pi(_valid_payload())
        ctx = _stage_context(cfg, artifacts, pi, diff_text="+ x = 1\n")
        # Write the diff to disk (PrepareRepositoryStage normally does this).
        artifacts.diff.write_text("+ x = 1\n", encoding="utf-8")
        # Seed a linked work item with an AC not reflected in the diff.
        builder.write_json(
            artifacts.work_items,
            [
                {
                    "id": 7,
                    "type": "User Story",
                    "title": "Charge flow",
                    "description": "...",
                    "acceptanceCriteria": "Update src/missing.py to handle the new field",
                }
            ],
        )
        builder.write_json(artifacts.changed_files, [{"file": "a.py"}])
        # FastReviewStage must run first so final-findings.json exists.
        FastReviewStage()(ctx)
        from reviewforge.pipeline.stages import AcceptanceCriteriaCoverageStage

        ac_result = AcceptanceCriteriaCoverageStage()(ctx)
        assert ac_result.status == StageStatus.OK
        assert ac_result.details["uncovered"] == 1
        final = builder.read_json(artifacts.final)
        assert len(final["findings"]) == 2
        assert final["findings"][0]["title"] == "Missing input validation"
        assert "Work item #7" in final["findings"][1]["title"]
