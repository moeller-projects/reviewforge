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
from conftest import FakePopen  # noqa: E402
from reviewforge.ai.runner import PiCliRunner  # noqa: E402
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
    _build_synthesis_instruction,
    _diff_chunks,
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
            "work_type": "change",
            "biggest_unknown": None,
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
    def test_regression_defaults_false(self):
        result = ReviewResult.model_validate(_valid_review_result_payload())
        assert result.findings[0].regression is False

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

class TestChunkSynthesisHelpers:
    def test_synthesis_instruction_lists_findings_and_uncertainties(self):
        text = _build_synthesis_instruction(
            3,
            [{"severity": "major", "title": "Bug", "file": "a.py", "line": 7}],
            [{"topic": "Rollout risk"}],
            pr_title="Prevent duplicate payment processing",
            pr_description="Add idempotency checks to the charge endpoint.",
            changed_files=["src/payments/charge.py", "tests/test_charge.py"],
        )
        assert "3 coherent diff chunks" in text
        assert "[major] Bug (a.py:7)" in text
        assert "Prevent duplicate payment processing" in text
        assert "Add idempotency checks to the charge endpoint." in text
        assert "- src/payments/charge.py" in text
        assert "- tests/test_charge.py" in text

    def test_synthesis_instruction_supplies_framing_for_clean_pr(self):
        text = _build_synthesis_instruction(
            2,
            [],
            [],
            pr_title="Refresh package constraints",
            pr_description="Update dependencies for a security release.",
            changed_files=["pyproject.toml", "uv.lock"],
        )

        assert "Refresh package constraints" in text
        assert "Update dependencies for a security release." in text
        assert "- pyproject.toml" in text
        assert "- uv.lock" in text


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
        assert ctx.artifacts.sarif.exists()
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

    def test_sarif_failure_does_not_fail_review(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path).with_overrides(reasoning_engine="single_pi")
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, _valid_review_result_payload()
        )
        ctx = _stage_context(cfg, pi)
        monkeypatch.setattr(
            "reviewforge.pipeline.stages.execute_reasoning_engine.review_result_to_sarif",
            lambda *args, **kwargs: (_ for _ in ()).throw(RuntimeError("sarif boom")),
        )
        result = ExecuteReasoningEngineStage()(ctx)
        assert result.status == StageStatus.OK
        assert "sarif_findings" not in result.details
        assert ctx.artifacts.review_result.exists()

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

    def test_returns_result_without_writing_artifacts(self, tmp_path: Path):
        # Artifact persistence is owned by ExecuteReasoningEngineStage so
        # that feedback filtering applies before anything hits disk.
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, _valid_review_result_payload()
        )
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        result = SinglePiReasoningEngine().execute(ctx)

        assert not ctx.artifacts.review_result.exists()
        assert not ctx.artifacts.final.exists()
        assert not any(path.exists() for path in (
            ctx.artifacts.intent, ctx.artifacts.plan, ctx.artifacts.collected,
            ctx.artifacts.digest, ctx.artifacts.candidate, ctx.artifacts.verified,
            ctx.artifacts.severity,
        ))
        assert result.findings[0].confidence == "high"

    def test_missing_json_raises_reasoning_engine_error(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: out.write_text("", encoding="utf-8")
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        engine = SinglePiReasoningEngine()
        with pytest.raises(ReasoningEngineError, match="produced no JSON"):
            engine.execute(ctx)

    def test_missing_model_framing_raises_schema_validation_error(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        pi.run_json.side_effect = lambda p, s, out, st: builder.write_json(
            out, {"findings": [], "uncertainties": []}
        )
        pi.last_tokens = {}
        ctx = _stage_context(cfg, pi)

        with pytest.raises(SchemaValidationError, match="required model framing"):
            SinglePiReasoningEngine().execute(ctx)

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

    def test_disable_chunk_review_forces_single_pass_even_over_max_bytes(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=10, chunk_trigger_diff_bytes=1, disable_chunk_review=True)
        pi = MagicMock()
        pi.run_json.side_effect = lambda _p, _s, out, _st: builder.write_json(out, _valid_review_result_payload())
        pi.last_tokens = {"in": 1, "out": 1, "total": 2}
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = "diff --git a/a.py b/a.py\n" + ("+x\n" * 100)

        result = SinglePiReasoningEngine().execute(ctx)

        assert len(result.metrics.chunkTokenUsage) == 0
        assert pi.run_json.call_count == 1

    def test_chunk_trigger_threshold_prevents_chunking_for_small_diffs(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=10, chunk_trigger_diff_bytes=10_000, disable_chunk_review=False)
        pi = MagicMock()
        pi.run_json.side_effect = lambda _p, _s, out, _st: builder.write_json(out, _valid_review_result_payload())
        pi.last_tokens = {"in": 1, "out": 1, "total": 2}
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = "diff --git a/a.py b/a.py\n" + ("+x\n" * 100)

        SinglePiReasoningEngine().execute(ctx)

        assert pi.run_json.call_count == 1

    def test_chunked_execution_dedupes_findings(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1)
        pi = MagicMock()
        payload = _valid_review_result_payload()
        partials = [
            {
                "findings": [{**payload["findings"][0], "title": "Missing input validation."}],
                "uncertainties": [],
            },
            {
                "findings": [{**payload["findings"][0], "title": "Missing input validation!"}],
                "uncertainties": [],
            },
        ]
        calls = []

        def fake_run_json(_p, _s, out, stage):
            if stage == "single-pi synthesis":
                builder.write_json(out, {
                    "review_summary": {"summary": "Solid chunk synthesis."},
                    "verification_summary": {"summary": "Verified per chunk."},
                    "pr_summary": {
                        "intent": "Did the thing.",
                        "work_type": "change",
                        "biggest_unknown": None,
                        "implementation_summary": "Did the thing.",
                    },
                })
                return
            builder.write_json(out, partials[len(calls)])
            calls.append(_s)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert len(result.findings) == 1
        assert result.metrics.chunkCount == 2
        assert result.review_summary.summary == "Solid chunk synthesis."
        assert result.pr_summary.implementation_summary == "Did the thing."
        assert ReviewResult.model_validate(result.model_dump())

    def test_chunked_execution_repeats_shared_context_and_records_usage(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1, pi_session_enabled=False)
        pi = MagicMock()
        prompts: list[str] = []
        synthesis_prompts: list[str] = []
        token_usage = [
            {"in": 10, "out": 5, "total": 15},
            {"in": 17, "out": 7, "total": 24},
            {"in": 25, "out": 10, "total": 35},
        ]
        payload = _valid_review_result_payload()["findings"][0]
        partial = {"findings": [payload], "uncertainties": []}

        def fake_run_json(_prompt_path, stdin, out, stage):
            if stage == "single-pi synthesis":
                synthesis_prompts.append(stdin)
                pi.token_usage = token_usage[2]
                builder.write_json(out, {
                    "review_summary": {"summary": "Synthesized."},
                    "pr_summary": {
                        "intent": "Add a helper.",
                        "work_type": "change",
                        "biggest_unknown": None,
                        "implementation_summary": "Clean change.",
                    },
                })
                return
            idx = len(prompts)
            prompts.append(stdin)
            pi.token_usage = token_usage[idx]
            builder.write_json(out, partial)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.metadata = {
            "repository": "payments",
            "pullRequestId": 42,
            "title": "Add payment idempotency",
            "description": "Reject duplicate payment submissions.",
        }
        ctx.files_text = "a.py\nb.py\n"
        ctx.extras["wi_context"] = [{"id": 7}]
        ctx.extras["thread_context"] = [{"id": 9}]
        ctx.extras["review_context"] = {"previousFeedback": [{"title": "Old issue"}]}
        ctx.extras["crg_analysis"] = {
            "status": "ok",
            "summary": "deterministic graph context",
            "risk_score": 0.4,
        }
        ctx.state.files = ["a.py", "b.py"]
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert "Add payment idempotency" in synthesis_prompts[0]
        assert "Reject duplicate payment submissions." in synthesis_prompts[0]
        assert "- a.py" in synthesis_prompts[0]
        assert "- b.py" in synthesis_prompts[0]
        assert len(prompts) == 2
        assert prompts[0].startswith("Single-call reasoning review for Azure DevOps PR #42.")
        assert "Repository/project metadata:" in prompts[0]
        assert "Changed files:" in prompts[0]
        assert "Previous review feedback:" in prompts[0]
        assert "Treat fixed findings as addressed, but flag them when reintroduced and set regression=true." in prompts[0]
        assert "Single-call reasoning review for Azure DevOps PR #42." in prompts[1]
        assert "Repository/project metadata:" in prompts[1]
        assert "Changed files:" in prompts[1]
        assert "Deterministic graph context (Tree-sitter code-review graph):" in prompts[1]
        assert [u.model_dump() for u in result.metrics.chunkTokenUsage] == [
            {"input": 10, "output": 5, "total": 15},
            {"input": 7, "output": 2, "total": 9},
        ]
        assert result.metadata.tokens.model_dump() == {"input": 25, "output": 10, "total": 35}

    def test_chunked_execution_aggregates_forked_runner_telemetry(self, tmp_path: Path, monkeypatch):
        cfg = replace(
            _cfg(tmp_path),
            max_diff_bytes=55,
            chunk_trigger_diff_bytes=1,
            chunk_synthesis_prompt_path=_cfg(tmp_path).fast_review_prompt_path,
        )
        partial = b'{"findings":[],"uncertainties":[]}'
        synthesis = json.dumps({
            "review_summary": {"summary": "Synthesized."},
            "pr_summary": {
                "intent": "Add a helper.",
                "work_type": "change",
                "biggest_unknown": None,
                "implementation_summary": "Clean change.",
            },
        }).encode()
        scope_attempts: dict[str, int] = {}

        def fake_popen(cmd, **_kwargs):
            session_id = cmd[cmd.index("--session-id") + 1]
            scope_attempts[session_id] = scope_attempts.get(session_id, 0) + 1
            if session_id.endswith("-scope-1"):
                return FakePopen(cmd, stdout=partial, stderr=b"tokens: 10 in / 5 out")
            if session_id.endswith("-scope-2") and scope_attempts[session_id] == 1:
                return FakePopen(cmd, stdout=b"not json", stderr=b"tokens: 20 in / 10 out")
            if session_id.endswith("-scope-2"):
                return FakePopen(cmd, stdout=partial, stderr=b"tokens: 3 in / 2 out")
            return FakePopen(cmd, stdout=synthesis, stderr=b"tokens: 30 in / 15 out")

        monkeypatch.setattr("reviewforge.ai.runner.subprocess.Popen", fake_popen)
        ctx = _stage_context(cfg, PiCliRunner(cfg))
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        expected_tokens = {"input": 63, "output": 32, "total": 95}
        assert result.metadata.tokens.model_dump() == expected_tokens
        assert result.metrics.piInputTokens == 63
        assert result.metrics.piOutputTokens == 32
        assert result.metrics.piTotalTokens == 95
        assert result.metrics.invocationCount == 4
        assert result.metrics.repairInvocationCount == 1
        assert ctx.last_token_usage == {"in": 63, "out": 32, "total": 95}

    def test_chunked_synthesis_failure_falls_back_to_boilerplate(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1)
        pi = MagicMock()
        partial = {"findings": [], "uncertainties": []}

        def fake_run_json(_p, _s, out, stage):
            if stage == "single-pi synthesis":
                raise ReasoningEngineError("pi exploded", details={})
            builder.write_json(out, partial)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert result.review_summary.summary == "Reviewed 2 coherent diff chunks."
        assert ctx.extras["_synthesis_fallback"] is True
        assert ReviewResult.model_validate(result.model_dump())

    def test_chunked_synthesis_invalid_json_falls_back(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1)
        pi = MagicMock()
        partial = {"findings": [], "uncertainties": []}

        def fake_run_json(_p, _s, out, stage):
            if stage == "single-pi synthesis":
                builder.write_json(out, {"not": "a synthesis"})
                return
            builder.write_json(out, partial)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert result.review_summary.summary == "Reviewed 2 coherent diff chunks."
        assert ctx.extras["_synthesis_fallback"] is True

    def test_synthesis_fallback_flag_reaches_stage_details(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1)
        pi = MagicMock()
        partial = {"findings": [], "uncertainties": []}

        def fake_run_json(_p, _s, out, stage):
            if stage == "single-pi synthesis":
                raise ReasoningEngineError("pi exploded", details={})
            builder.write_json(out, partial)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        details = ExecuteReasoningEngineStage().run(ctx)

        assert details["synthesisFallback"] is True



class TestChunkSectionMerge:
    def test_merges_gaps_hints_and_discarded(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), max_diff_bytes=55, chunk_trigger_diff_bytes=1)
        pi = MagicMock()
        partials = [
            {
                "findings": [],
                "test_gaps": [{"behavior": "edge case", "suggested_test": "add test", "file": "a.py"}],
                "uncertainties": [],
                "escalation_hints": [
                    {"files": ["a.py"], "reason": "auth path", "suggested_focus": "security-audit", "danger": "critical"}
                ],
                "discarded_findings": [{"reason": "out of scope", "category": "out-of-scope", "count": 1}],
            },
            {
                "findings": [],
                "test_gaps": [{"behavior": "edge case", "suggested_test": "add test", "file": "a.py"}],
                "uncertainties": [],
                "escalation_hints": [
                    {"files": ["a.py"], "reason": "auth path", "suggested_focus": "security-audit", "danger": "critical"}
                ],
                "discarded_findings": [{"reason": "out of scope", "category": "out-of-scope", "count": 1}],
            },
        ]
        calls = []

        def fake_run_json(_p, _s, out, stage):
            if stage == "single-pi synthesis":
                builder.write_json(out, {
                    "review_summary": {"summary": "Synthesized."},
                    "pr_summary": {
                        "intent": "Add auth.",
                        "work_type": "feature",
                        "biggest_unknown": None,
                        "implementation_summary": "Add auth.",
                    },
                })
                return
            builder.write_json(out, partials[len(calls)])
            calls.append(_s)

        pi.run_json.side_effect = fake_run_json
        pi.token_usage = {"in": 0, "out": 0, "total": 0}
        pi.invocation_count = 0
        pi.repair_invocation_count = 0
        ctx = _stage_context(cfg, pi)
        ctx.state.diff_text = (
            "diff --git a/a.py b/a.py\n+@@ -1 +1 @@\n-old\n+new\n"
            "diff --git a/b.py b/b.py\n+@@ -1 +1 @@\n-old\n+new\n"
        )

        result = SinglePiReasoningEngine().execute(ctx)

        assert len(result.test_gaps) == 1
        assert len(result.escalation_hints) == 1
        assert result.escalation_hints[0].danger == "critical"
        assert len(result.discarded_findings) == 1


class TestDeterministicNormalization:
    def _payload(self, *, biggest_unknown: str | None) -> dict:
        payload = _valid_review_result_payload()
        payload["pr_summary"]["biggest_unknown"] = biggest_unknown
        payload["pr_summary"]["architectural_impact"] = "invented impact"
        return payload

    def test_missing_architecture_is_normalized_and_confidence_downgrades(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        payload = self._payload(biggest_unknown="reachability unclear")
        pi.run_json.side_effect = lambda _p, _s, out, _st: builder.write_json(out, payload)
        pi.last_tokens = {"in": 1, "out": 1, "total": 2}
        ctx = _stage_context(cfg, pi)

        result = SinglePiReasoningEngine().execute(ctx)

        assert result.pr_summary.architectural_impact == "no significant architectural impact"
        assert result.review_confidence.level == "medium"
        assert result.metrics.confidence == "medium"


class TestEscalationPolicy:
    def test_escalation_disabled_makes_no_extra_call(self, tmp_path: Path):
        cfg = _cfg(tmp_path)
        pi = MagicMock()
        payload = _valid_review_result_payload()
        payload["escalation_hints"] = [
            {"files": ["a.py"], "reason": "auth path", "suggested_focus": "security-audit", "danger": "critical"}
        ]
        pi.run_json.side_effect = lambda _p, _s, out, _st: builder.write_json(out, payload)
        pi.last_tokens = {"in": 1, "out": 1, "total": 2}
        ctx = _stage_context(cfg, pi)

        result = SinglePiReasoningEngine().execute(ctx)

        assert len(result.escalation_hints) == 1
        assert pi.run_json.call_count == 1

    def test_escalation_enabled_runs_focused_pass(self, tmp_path: Path):
        cfg = replace(_cfg(tmp_path), escalation_review_enabled=True)
        pi = MagicMock()
        payload = _valid_review_result_payload()
        payload["escalation_hints"] = [
            {"files": ["a.py"], "reason": "auth path", "suggested_focus": "security-audit", "danger": "critical"}
        ]

        def fake_run_json(_p, _s, out, stage):
            if stage == "escalation review":
                builder.write_json(out, _valid_review_result_payload())
                return
            builder.write_json(out, payload)

        pi.run_json.side_effect = fake_run_json
        pi.last_tokens = {"in": 1, "out": 1, "total": 2}
        ctx = _stage_context(cfg, pi)

        result = SinglePiReasoningEngine().execute(ctx)

        assert pi.run_json.call_count == 2
        assert len(result.findings) == 1

def _write_debug_first_pass(c):
    for path, n in [
        (c.artifacts.intent, 0),
        (c.artifacts.plan, 0),
        (c.artifacts.collected, 0),
        (c.artifacts.digest, 0),
        (c.artifacts.candidate, 5),
        (c.artifacts.verified, 3),
    ]:
        builder.write_json(path, {"summary": "", "findings": [{"i": i} for i in range(n)]})
    severity_finding = {
        "title": "Bug",
        "message": "It breaks.",
        "severity": "minor",
        "suggestion": "Fix it.",
        "file": "b.py",
        "line": 5,
        "evidence": {
            "changedLines": [5],
            "contextFilesRead": ["b.py"],
            "whyNewInThisPr": "Introduced here.",
            "whyNotIntentional": "No equivalent guard exists.",
        },
    }
    builder.write_json(c.artifacts.severity, {"summary": "", "findings": [severity_finding]})
    c.intent = {"pr_intent": "debug", "risk_areas": []}
    c.plan = {"pr_intent": "debug", "files_to_read": [], "searches_to_run": [], "tests_to_inspect": []}
    c.collected = {"tests": []}
    c.digest = {"possible_intentional_choices": []}
    c.candidate = {"summary": "", "findings": [{"i": i} for i in range(5)]}
    c.verified = {"summary": "", "findings": [{"i": i} for i in range(3)]}
    c.severity = {"summary": "", "findings": [severity_finding]}


def _debug_intermediate_stages(call_count: list[int]):
    def fake_run_stages(stages, c):
        call_count[0] += 1
        if call_count[0] == 1:
            _write_debug_first_pass(c)
        return [
            StageResult(name=s.name, status=StageStatus.OK, started_at="t1", finished_at="t2", duration_ms=1)
            for s in stages
        ]

    return fake_run_stages


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
                "whyNewInThisPr": "Introduced here.",
                "whyNotIntentional": "No equivalent guard exists.",
            },
        }
        rich = MultiStageReasoningEngine._legacy_to_rich(legacy)
        assert rich.title == "Bug"
        assert rich.file == "b.py"
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

        monkeypatch.setattr("reviewforge.reasoning.multi_stage.run_stages", fake_run_stages)

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
            for path in (
                c.artifacts.intent,
                c.artifacts.plan,
                c.artifacts.collected,
                c.artifacts.digest,
                c.artifacts.candidate,
                c.artifacts.verified,
                c.artifacts.severity,
            ):
                builder.write_json(path, {"summary": "", "findings": []})
            c.intent = {"pr_intent": "debug", "risk_areas": []}
            c.severity = {"summary": "", "findings": []}
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

        monkeypatch.setattr("reviewforge.reasoning.multi_stage.run_stages", fake_run_stages)
        MultiStageReasoningEngine().execute(ctx)
        assert all(path.exists() for path in (
            ctx.artifacts.intent, ctx.artifacts.plan, ctx.artifacts.collected,
            ctx.artifacts.digest, ctx.artifacts.candidate, ctx.artifacts.verified,
            ctx.artifacts.severity,
        ))

    def test_debug_intermediates_captures_fragment_counts(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, MagicMock())
        call_count = [0]
        fake_run_stages = _debug_intermediate_stages(call_count)

        monkeypatch.setattr("reviewforge.reasoning.multi_stage.run_stages", fake_run_stages)
        MultiStageReasoningEngine().execute(ctx)
        assert call_count[0] == 2
        assert ctx.extras["_finding_counts"] == {
            "candidate": 5,
            "verified": 3,
            "severity": 1,
            "final": 1,
        }
        assert not any(path.exists() for path in (
            ctx.artifacts.candidate, ctx.artifacts.verified, ctx.artifacts.severity,
        ))

    def test_execute_propagates_failure(self, tmp_path: Path, monkeypatch):
        cfg = _cfg(tmp_path)
        ctx = _stage_context(cfg, MagicMock())
        calls = []

        def fake_run_stages(stages, c):
            calls.append([s.name for s in stages])
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

        monkeypatch.setattr("reviewforge.reasoning.multi_stage.run_stages", fake_run_stages)

        engine = MultiStageReasoningEngine()
        with pytest.raises(ReasoningEngineError, match="reconstruct_intent failed"):
            engine.execute(ctx)
        assert len(calls) == 1

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

    def test_symbol_files_dedupes_and_skips_missing(self):
        from reviewforge.pipeline.projection import _symbol_files
        from reviewforge.pipeline.schemas import RichSymbol

        symbols = [
            RichSymbol.model_validate({"name": "a", "file": "x.py"}),
            RichSymbol.model_validate({"name": "b", "file": "x.py"}),
            RichSymbol.model_validate({"name": "c"}),
            RichSymbol.model_validate({"name": "d", "file": "y.py"}),
        ]
        assert _symbol_files(symbols) == ["x.py", "y.py"]



class TestCrgPromptInjection:
    """Regression tests for the deterministic graph-context prompt block."""

    def _crg_document(self, **overrides) -> dict[str, Any]:
        doc = {
            "status": "ok",
            "tool_version": "1.0.0",
            "build": {"mode": "full", "duration_ms": 10},
            "summary": "Analyzed 2 files",
            "risk_score": 0.5,
            "changed_functions": [],
            "affected_flows": [],
            "test_gaps": [],
            "impacted_files": [],
            "review_priorities": [],
        }
        doc.update(overrides)
        return doc

    def test_absent_or_failed_analysis_is_byte_identical(self, tmp_path):
        """Absent/failed graph context must not alter the instruction at all."""
        from reviewforge.reasoning.single_pi import (
            _build_single_pi_instruction,
            _build_single_pi_prefix,
        )

        ctx = _stage_context(_cfg(tmp_path), MagicMock())
        baseline_prefix = _build_single_pi_prefix(ctx)
        baseline_instruction = _build_single_pi_instruction(ctx)
        assert "graph context" not in baseline_instruction

        for status in ("failed", "unavailable", "degraded-but-junk"):
            ctx.extras["crg_analysis"] = self._crg_document(status=status)
            assert _build_single_pi_prefix(ctx) == baseline_prefix
            assert _build_single_pi_instruction(ctx) == baseline_instruction

        # Junk payloads likewise leave the prompt untouched.
        for junk in ({}, {"status": "ok"}, None):
            ctx.extras["crg_analysis"] = junk
            assert _build_single_pi_prefix(ctx) == baseline_prefix

    def test_ok_analysis_injects_section_before_diff(self, tmp_path):
        from reviewforge.reasoning.single_pi import _build_single_pi_instruction

        ctx = _stage_context(_cfg(tmp_path), MagicMock())
        ctx.extras["crg_analysis"] = self._crg_document()
        instruction = _build_single_pi_instruction(ctx)
        assert "Deterministic graph context" in instruction
        assert instruction.index("Deterministic graph context") < instruction.index("Unified diff:")

    def test_degraded_analysis_is_injected_with_truncation_note(self, tmp_path):
        from reviewforge.reasoning.single_pi import _build_single_pi_prefix

        ctx = _stage_context(_cfg(tmp_path), MagicMock())
        ctx.extras["crg_analysis"] = self._crg_document(status="degraded")
        prefix = _build_single_pi_prefix(ctx)
        assert "Deterministic graph context" in prefix
        assert "truncated" in prefix

    def test_subsection_caps_and_deterministic_ordering(self, tmp_path):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        priorities = [
            {"qualified_name": f"mod.p{i}", "file": f"src/p{i}.py", "risk_score": i / 100.0}
            for i in range(8)
        ]
        functions = [
            {"qualified_name": f"mod.f{i}", "file": f"src/f{i}.py", "risk_score": i / 100.0}
            for i in range(20)
        ]
        impacted = [f"src/z{i}.py" for i in range(40)]
        gaps = [{"qualified_name": f"mod.g{i}", "file": f"src/g{i}.py"} for i in range(20)]
        doc = self._crg_document(
            review_priorities=priorities,
            changed_functions=functions,
            impacted_files=impacted,
            test_gaps=gaps,
        )
        text = build_crg_section(doc, 1_000_000)
        lines = text.splitlines()

        def section(name: str) -> list[str]:
            start = lines.index(name) + 1
            out = []
            for line in lines[start:]:
                if not line.startswith("  - "):
                    break
                out.append(line)
            return out

        assert len(section("Review priorities (highest risk first):")) == 5
        assert len(section("Changed functions (highest risk first):")) == 15
        assert len(section("Impacted files:")) == 30
        assert len(section("Functions without test coverage:")) == 15
        # Risk descending within function lists.
        assert "mod.p7" in section("Review priorities (highest risk first):")[0]
        assert "mod.f19" in section("Changed functions (highest risk first):")[0]
        # Impacted files ascending by path.
        paths = [line.removeprefix("  - ") for line in section("Impacted files:")]
        assert paths == sorted(paths)
        # Deterministic: same input, same output.
        assert build_crg_section(doc, 1_000_000) == text

    def test_byte_cap_is_respected(self, tmp_path):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        doc = self._crg_document(summary="x" * 5000)
        text = build_crg_section(doc, 256)
        assert len(text.encode("utf-8")) <= 256
        # Multibyte content must not be split mid-codepoint.
        doc = self._crg_document(summary="é" * 500)
        text = build_crg_section(doc, 255)
        assert len(text.encode("utf-8")) <= 255
    def test_wave2_context_drops_malformed_entries_and_respects_cap(self):
        from reviewforge.pipeline.crg.prompt import build_wave2_section

        context = {
            "api_surface": {
                "status": "ok",
                "breaking_candidates": ["bad", {"symbol": "pkg.api", "caller_count": 2}],
                "added_nodes": ["pkg.added"],
                "removed_nodes": ["pkg.removed"],
            },
            "flows": {
                "top": [
                    {"entry_point": "bad", "criticality": "not-a-number"},
                    {"entry_point": "handler", "criticality": 0.75},
                ]
            },
            "architecture": {
                "hubs_touched": ["bad", {"qualified_name": "pkg.hub"}],
                "bridges_touched": [None, {"qualified_name": "pkg.bridge"}],
                "communities_crossed": 1,
            },
        }
        text = build_wave2_section(context, 256)
        assert "pkg.api" in text or "handler" in text or "pkg.hub" in text
        assert len(text.encode("utf-8")) <= 256


    def test_cfg_byte_cap_flows_into_prefix(self, tmp_path):
        from dataclasses import replace as _replace

        from reviewforge.reasoning.single_pi import _build_single_pi_prefix

        cfg = _replace(_cfg(tmp_path), crg_context_max_bytes=64)
        ctx = _stage_context(cfg, MagicMock())
        ctx.extras["crg_analysis"] = self._crg_document(summary="y" * 1000)
        prefix = _build_single_pi_prefix(ctx)
        block = prefix.split("Deterministic graph context (Tree-sitter code-review graph):\n", 1)[1]
        assert len(block.encode("utf-8")) <= 64

    def test_chunked_prompt_repeats_graph_context_only_with_shared_prefix(self, tmp_path):
        from reviewforge.reasoning.single_pi import _build_chunk_instruction

        ctx = _stage_context(_cfg(tmp_path), MagicMock())
        ctx.extras["crg_analysis"] = self._crg_document()
        chunk1 = _build_chunk_instruction(ctx, "diff --git a/a.py", 1, 2, include_shared_prefix=False)
        chunk2 = _build_chunk_instruction(ctx, "diff --git a/b.py", 2, 2, include_shared_prefix=False)
        assert "Deterministic graph context" in chunk1
        assert "Deterministic graph context" not in chunk2

    def test_empty_analysis_formats_to_empty_string(self):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        assert build_crg_section({}, 8192) == ""

    def test_affected_flows_are_listed(self):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        doc = self._crg_document(affected_flows=["flow_a", "flow_b"])
        text = build_crg_section(doc, 8192)
        assert "Affected flows: flow_a, flow_b" in text

    def test_malformed_entries_are_dropped_not_fatal(self):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        doc = self._crg_document(
            changed_functions=[
                "garbage",
                {"qualified_name": "mod.ok", "file": "src/ok.py", "risk_score": "high"},
                None,
                {"name": "mod.nonnumeric", "file_path": "src/nn.py", "risk_score": "high"},
            ],
            review_priorities=[{"qualified_name": "mod.bad", "risk_score": "not-a-number"}],
            risk_score="not-a-number",
        )
        text = build_crg_section(doc, 8192)
        assert "mod.ok" in text
        assert "mod.nonnumeric" in text
        assert "risk=0.00" in text
        assert "Overall risk score" not in text

    def test_non_positive_cap_disables_injection(self, tmp_path):
        from dataclasses import replace as _replace
        from reviewforge.reasoning.single_pi import _build_single_pi_prefix
        from reviewforge.pipeline.crg.prompt import build_crg_section

        doc = self._crg_document()
        assert build_crg_section(doc, 0) == ""
        assert build_crg_section(doc, -5) == ""
        cfg = _replace(_cfg(tmp_path), crg_context_max_bytes=0)
        ctx = _stage_context(cfg, MagicMock())
        baseline = _build_single_pi_prefix(ctx)
        ctx.extras["crg_analysis"] = doc
        assert _build_single_pi_prefix(ctx) == baseline

    def test_non_object_analysis_is_ignored(self):
        from reviewforge.pipeline.crg.prompt import build_crg_section

        assert build_crg_section(["junk"], 8192) == ""
