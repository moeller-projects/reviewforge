"""Tests for the reasoning-engine abstraction."""
from __future__ import annotations

import json
from dataclasses import replace
import sys
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
from reviewforge.exceptions import ReasoningEngineError, ReviewForgeError, SchemaValidationError  # noqa: E402
from reviewforge.pipeline.schemas import (  # noqa: E402
    ReviewResult,
)
from reviewforge.pipeline.stage import StageContext, StageResult, StageStatus  # noqa: E402
from reviewforge.pipeline.stages import ExecuteReasoningEngineStage  # noqa: E402
from reviewforge.pipeline.validation import validate_postable_review_doc  # noqa: E402
from reviewforge.reasoning.engine import (  # noqa: E402
    ReasoningEngine,
    _ENGINE_REGISTRY,
    get_engine,
    register_engine,
)
from reviewforge.reasoning.multi_stage import MultiStageReasoningEngine  # noqa: E402
from reviewforge.reasoning.single_pi import (  # noqa: E402
    SinglePiReasoningEngine,
    _build_single_pi_instruction,
    _diff_chunks,
    _reduce_diff,
)

def _cfg(tmp_path: Path) -> Config:
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
    )


def _make_state(diff_text: str = "+ x = 1\n", files: list[str] | None = None):
    return SimpleNamespace(
        diff_text=diff_text,
        files=files or ["a.py"],
        target_branch="main",
        source_branch="feature",
        target_commit="abc",
        source_commit="def",
    )


def _stage_context(cfg: Config, pi: MagicMock) -> StageContext:
    artifacts = manager.create(cfg)
    builder.write_json(artifacts.metadata, {"status": "active", "isDraft": False})
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=_make_state(), pi=pi)
    ctx.files_text = "a.py\n"
    ctx.extras["wi_context"] = []
    ctx.extras["thread_context"] = []
    return ctx


def _valid_review_result_payload() -> dict[str, Any]:
    return {
        "review_summary": {"summary": "Clean change.", "notes": ""},
        "verification_summary": {
            "summary": "Verified by reading surrounding code.",
            "approach": "read surrounding code",
            "notes": "",
        },
        "pr_summary": {
            "intent": "Add a new helper.",
            "implementation_summary": "Clean change.",
            "architectural_impact": "",
            "risk_assessment": "",
            "positive_observations": [],
        },
        "findings": [
            {
                "severity": "major",
                "title": "Missing input validation",
                "observation": "The helper does not validate input.",
                "impact": "Invalid input may cause failures.",
                "recommendation": "Add validation.",
                "confidence": "high",
                "file": "a.py",
                "line": 10,
                "contextBasis": "surrounding-code-read",
                "evidence": {
                    "changedLines": [10],
                    "relatedFiles": ["a.py"],
                    "testsRead": ["tests/test_a.py"],
                    "workItems": [],
                    "symbols": [],
                    "whyNewInThisPr": "Introduced in this PR.",
                    "whyNotIntentional": "No guard elsewhere.",
                },
            }
        ],
        "discarded_findings": [],
        "good_practices": [],
        "uncertainties": [],
        "metrics": {
            "changedFilesReviewed": 1,
            "filesIgnored": 0,
            "testsRead": 1,
            "symbolsInspected": 0,
            "workItemsRead": 0,
            "confidence": "high",
            "reviewDepth": "deep",
        },
        "review_confidence": {
            "level": "high",
            "reasons": ["single-pass reasoning with embedded verification"],
        },
    }


class TestCanonicalReviewResultContract:
    def test_default_result_is_valid(self):
        result = ReviewResult()
        assert result.review_summary.summary
        assert result.verification_summary.summary

    def test_empty_recommendation_is_rejected(self):
        payload = _valid_review_result_payload()
        payload["findings"][0]["recommendation"] = ""
        with pytest.raises(Exception, match="non-empty"):
            ReviewResult.model_validate(payload)

    def test_empty_evidence_is_rejected(self):
        payload = _valid_review_result_payload()
        payload["findings"][0]["evidence"] = {}
        with pytest.raises(Exception, match="reference"):
            ReviewResult.model_validate(payload)

    @pytest.mark.parametrize(
        ("diff", "limit"),
        [
            ("", 0),
            ("diff --git a/a.py b/a.py\n@@ -1 +1 @@\n+é\n", 1),
            ("diff --git a/a.py b/a.py\n@@ -1 +1 @@\n+changed\n", 24),
            (
                "diff --git a/a.py b/a.py\n@@ -1 +1 @@\n+one\n"
                "diff --git a/b.py b/b.py\n@@ -1 +1 @@\n+two\n",
                20,
            ),
            (
                "".join(
                    f"diff --git a/file{i}.py b/file{i}.py\n@@ -1 +1 @@\n+change{i}\n"
                    for i in range(100)
                ),
                17,
            ),
        ],
    )
    def test_diff_reduction_never_exceeds_limit(self, diff, limit):
        reduced, _ = _reduce_diff(diff, limit)
        assert len(reduced.encode("utf-8")) <= limit

    def test_diff_reduction_preserves_headers_when_budget_allows(self):
        diff = (
            "diff --git a/a.py b/a.py\n@@ -1 +1 @@\n+a\n"
            "diff --git a/b.py b/b.py\n@@ -1 +1 @@\n+b\n"
        )
        reduced, was_reduced = _reduce_diff(diff, 70)
        assert was_reduced is True
        assert "diff --git a/a.py b/a.py" in reduced
        assert "diff --git a/b.py b/b.py" in reduced

    def test_diff_reduction_is_deterministic(self):
        diff = "diff --git a/a.py b/a.py\n+é\n" * 20
        assert _reduce_diff(diff, 31) == _reduce_diff(diff, 31)

    def test_diff_at_exact_limit_is_unchanged(self):
        diff = "diff --git a/a.py b/a.py\n+é\n"
        reduced, was_reduced = _reduce_diff(diff, len(diff.encode("utf-8")))
        assert reduced == diff
        assert was_reduced is False

    def test_postable_projection_rejects_missing_evidence(self):
        with pytest.raises(ReviewForgeError, match="evidence"):
            validate_postable_review_doc(
                {
                    "summary": "review",
                    "findings": [
                        {
                            "severity": "major",
                            "title": "T",
                            "message": "M",
                            "suggestion": "Fix it.",
                        }
                    ],
                }
            )

class TestEngineRegistry:
    def test_built_in_engines_registered(self):
        assert "multi_stage" in _ENGINE_REGISTRY
        assert "single_pi" in _ENGINE_REGISTRY

    def test_get_engine_unknown_raises(self):
        with pytest.raises(ValueError, match="Unknown reasoning engine"):
            get_engine("no_such_engine", None)

    def test_get_engine_creates_instance(self):
        engine = get_engine("single_pi", None)
        assert isinstance(engine, SinglePiReasoningEngine)
        assert engine.name == "single_pi"

    def test_register_duplicate_allowed(self):
        class DummyEngine(ReasoningEngine):
            @property
            def name(self) -> str:
                return "dummy"

            def execute(self, ctx: StageContext) -> ReviewResult:
                return ReviewResult()

        register_engine("dummy", DummyEngine)
        assert _ENGINE_REGISTRY["dummy"] is DummyEngine


class TestExecuteReasoningEngineStage:
    def test_runs_single_pi_engine(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        cfg = cfg.with_overrides(reasoning_engine="single_pi")
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, _valid_review_result_payload()
        )
        pi.last_tokens = {"in": 100, "out": 50, "total": 150}
        ctx = _stage_context(cfg, pi)

        result = ExecuteReasoningEngineStage()(ctx)

        assert result.status == StageStatus.OK
        assert builder.read_json(ctx.artifacts.review_result)["metrics"]["piTotalTokens"] == 150
        assert result.details["engine"] == "single_pi"
        assert result.details["findings"] == 1
        assert ctx.artifacts.review_result.exists()
        assert ctx.artifacts.final.exists()
        final = builder.read_json(ctx.artifacts.final)
        assert len(final["findings"]) == 1
        assert final["findings"][0]["confidence"] == "high"

    def test_runs_multi_stage_engine(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path)
        cfg = cfg.with_overrides(reasoning_engine="multi_stage")
        pi = MagicMock()
        pi.last_tokens = {"in": 100, "out": 50, "total": 150}
        ctx = _stage_context(cfg, pi)

        def fake_run_stages(stages, c):
            c.intent = {"pr_intent": "Add a new helper.", "risk_areas": []}
            c.digest = {"possible_intentional_choices": []}
            c.final = {
                "summary": "Clean change.",
                "findings": [
                    {
                        "title": "Missing input validation",
                        "message": "The helper does not validate input.",
                        "severity": "major",
                        "file": "a.py",
                        "line": 10,
                        "suggestion": "Add validation.",
                        "contextBasis": "surrounding-code-read",
                        "evidence": {"changedLines": [10], "contextFilesRead": ["a.py"]},
                    }
                ],
            }
            return [
                StageResult(
                    name=s.name,
                    status=StageStatus.OK,
                    started_at="t1",
                    finished_at="t2",
                    duration_ms=1,
                )
                for s in stages
            ]

        monkeypatch.setattr(
            "reviewforge.reasoning.multi_stage.run_stages", fake_run_stages
        )

        result = ExecuteReasoningEngineStage()(ctx)

        assert result.status == StageStatus.OK
        assert result.details["engine"] == "multi_stage"
        assert result.details["findings"] == 1
        assert ctx.artifacts.review_result.exists()
        assert ctx.artifacts.final.exists()

    def test_records_failure(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        cfg = cfg.with_overrides(reasoning_engine="single_pi")
        pi = MagicMock()
        pi.run_json.side_effect = RuntimeError("boom")
        ctx = _stage_context(cfg, pi)

        result = ExecuteReasoningEngineStage()(ctx)

        assert result.status == StageStatus.FAILED
        assert "boom" in result.error


class TestSinglePiReasoningEngine:
    def test_execute_writes_review_result(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, _valid_review_result_payload()
        )
        pi.last_tokens = {"in": 100, "out": 50, "total": 150}
        ctx = _stage_context(cfg, pi)

        engine = SinglePiReasoningEngine()
        result = engine.execute(ctx)

        assert result.pr_summary.intent == "Add a new helper."
        assert len(result.findings) == 1
        assert result.findings[0].title == "Missing input validation"
        assert result.findings[0].evidence.relatedFiles == ["a.py"]
        assert result.findings[0].confidence == "high"
        assert result.metrics.testsRead == 1
        assert result.metadata.model.reasoning_engine == "single_pi"
        assert result.metadata.tokens.total == 150

    def test_empty_findings_produces_valid_result(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        payload = _valid_review_result_payload()
        payload["findings"] = []
        payload["metrics"]["changedFilesReviewed"] = 0
        payload["metrics"]["testsRead"] = 0
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(out, payload)
        pi.last_tokens = {"in": 100, "out": 50, "total": 150}
        ctx = _stage_context(cfg, pi)

        engine = SinglePiReasoningEngine()
        result = engine.execute(ctx)

        assert result.findings == []
        assert result.review_confidence.level == "high"

    def test_writes_only_canonical_and_projection_artifacts(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, _valid_review_result_payload()
        )
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        result = SinglePiReasoningEngine().execute(ctx)

        assert ctx.artifacts.review_result.exists()
        assert ctx.artifacts.final.exists()
        assert not any(path.exists() for path in (
            ctx.artifacts.intent, ctx.artifacts.plan, ctx.artifacts.collected,
            ctx.artifacts.digest, ctx.artifacts.candidate, ctx.artifacts.verified,
            ctx.artifacts.severity,
        ))
        assert builder.read_json(ctx.artifacts.review_result) == result.model_dump(
            by_alias=True, exclude_none=False
        )
        final = builder.read_json(ctx.artifacts.final)
        assert final["summary"] == "Clean change."
        assert final["findings"][0]["confidence"] == "high"

    def test_missing_json_raises_reasoning_engine_error(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: out.write_text("", encoding="utf-8")
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        engine = SinglePiReasoningEngine()
        with pytest.raises(ReasoningEngineError, match="produced no JSON"):
            engine.execute(ctx)

    def test_invalid_schema_raises_schema_validation_error(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, {"not": "valid"}
        )
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        engine = SinglePiReasoningEngine()
        with pytest.raises(SchemaValidationError, match="ReviewResult schema"):
            engine.execute(ctx)

    def test_instruction_includes_bounded_commit_context(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), commit_context_max=1)
        pi = MagicMock()
        ctx = _stage_context(cfg, pi)
        ctx.artifacts.commits.write_text("abc first\nxyz second\n", encoding="utf-8")

        instruction = _build_single_pi_instruction(ctx)

        assert "Commits in this PR:\nabc first" in instruction
        assert "xyz second" not in instruction

    def test_diff_chunks_are_deterministic(self):
        diff = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )
        assert _diff_chunks(diff, 55) == _diff_chunks(diff, 55)

    def test_chunked_execution_dedupes_findings(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55)
        pi = MagicMock()
        payload = _valid_review_result_payload()
        partial = {"findings": payload["findings"], "uncertainties": []}
        pi.run_json.side_effect = lambda _p, _s, out, _stage: builder.write_json(out, partial)
        pi.last_tokens = {"in": 10, "out": 5, "total": 15}
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert len(result.findings) == 1
        assert result.metrics.chunkCount == 2
        assert ReviewResult.model_validate(result.model_dump())


class TestMultiStageReasoningEngine:
    def test_build_pr_summary(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, MagicMock())
        ctx.intent = {"pr_intent": "Add a helper.", "risk_areas": ["logic error"]}
        ctx.digest = {"possible_intentional_choices": ["clean design"]}

        summary = MultiStageReasoningEngine._build_pr_summary(ctx)
        assert summary.intent == "Add a helper."
        assert summary.risk_assessment == "logic error"
        assert summary.positive_observations == ["clean design"]

    def test_legacy_to_rich(self):
        legacy = {
            "title": "Bug",
            "message": "It breaks.",
            "severity": "minor",
            "file": "b.py",
            "line": 5,
            "contextBasis": "diff-only",
            "suggestion": "Fix it.",
            "evidence": {
                "changedLines": [5],
                "contextFilesRead": ["b.py"],
                "whyNewInThisPr": "New code.",
            },
        }
        rich = MultiStageReasoningEngine._legacy_to_rich(legacy)
        assert rich.title == "Bug"
        assert rich.observation == "It breaks."
        assert rich.recommendation == "Fix it."
        assert rich.evidence.relatedFiles == ["b.py"]

    def test_execute_with_stages(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, MagicMock())

        def fake_run_stages(stages, c):
            c.intent = {"pr_intent": "Add a helper.", "risk_areas": []}
            c.digest = {"possible_intentional_choices": []}
            c.final = {
                "summary": "Clean.",
                "findings": [
                    {
                        "title": "Bug",
                        "message": "It breaks.",
                        "severity": "minor",
                        "file": "b.py",
                        "line": 5,
                        "suggestion": "Fix it.",
                        "contextBasis": "diff-only",
                        "evidence": {"changedLines": [5]},
                    }
                ],
            }
            return [
                StageResult(
                    name=s.name,
                    status=StageStatus.OK,
                    started_at="t1",
                    finished_at="t2",
                    duration_ms=1,
                )
                for s in stages
            ]

        monkeypatch.setattr(
            "reviewforge.reasoning.multi_stage.run_stages", fake_run_stages
        )

        engine = MultiStageReasoningEngine()
        result = engine.execute(ctx)

        assert result.pr_summary.intent == "Add a helper."
        assert len(result.findings) == 1
        assert result.findings[0].severity == "minor"
        assert result.review_confidence.level == "high"
        assert result.metadata.model.reasoning_engine == "multi_stage"

    def test_debug_intermediates_retains_multi_stage_fragments(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path).with_overrides(debug_intermediates=True)
        ctx = _stage_context(cfg, MagicMock())

        def fake_run_stages(stages, c):
            for path in (c.artifacts.intent, c.artifacts.plan, c.artifacts.collected, c.artifacts.digest, c.artifacts.candidate, c.artifacts.verified, c.artifacts.severity):
                builder.write_json(path, {"summary": "", "findings": []})
            c.intent = {"pr_intent": "debug", "risk_areas": []}
            c.severity = {"summary": "", "findings": []}
            return [StageResult(name=s.name, status=StageStatus.OK, started_at="t1", finished_at="t2", duration_ms=1) for s in stages]

        monkeypatch.setattr("reviewforge.reasoning.multi_stage.run_stages", fake_run_stages)
        MultiStageReasoningEngine().execute(ctx)
        assert all(path.exists() for path in (
            ctx.artifacts.intent, ctx.artifacts.plan, ctx.artifacts.collected,
            ctx.artifacts.digest, ctx.artifacts.candidate, ctx.artifacts.verified,
            ctx.artifacts.severity,
        ))

    def test_execute_propagates_failure(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, MagicMock())

        def fake_run_stages(stages, c):
            return [
                StageResult(
                    name="reconstruct_intent",
                    status=StageStatus.FAILED,
                    error="bad",
                    started_at="t1",
                    finished_at="t2",
                    duration_ms=1,
                )
            ]

        monkeypatch.setattr(
            "reviewforge.reasoning.multi_stage.run_stages", fake_run_stages
        )

        engine = MultiStageReasoningEngine()
        with pytest.raises(ReasoningEngineError, match="reconstruct_intent failed"):
            engine.execute(ctx)


class TestProjection:
    def test_review_result_to_final_doc(self):
        from reviewforge.pipeline.projection import review_result_to_final_doc

        result = ReviewResult.model_validate(_valid_review_result_payload())
        final = review_result_to_final_doc(result)
        assert final["summary"] == "Clean change."
        assert len(final["findings"]) == 1
        assert final["findings"][0]["message"]
        assert final["findings"][0]["severity"] == "major"
        assert final["findings"][0]["confidence"] == "high"
        assert final["findings"][0]["suggestion"] == "Add validation."
        assert final["findings"][0]["evidence"]["contextFilesRead"] == ["a.py", "tests/test_a.py"]

